using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// 本地 RapidOCR/ONNX Worker 的启动和 stdio 协议配置。
/// 生产环境应将 Python 运行时和模型放入受安装器管理的目录，不能依赖用户全局 Python。
/// </summary>
public sealed record RapidOcrWorkerOptions(
    string PythonExecutable,
    string WorkerScriptPath,
    TimeSpan StartupTimeout = default,
    TimeSpan RecognitionTimeout = default,
    string? ModelId = null,
    string? DetectionModelPath = null,
    string? RecognitionModelPath = null,
    string? RecognitionKeysPath = null)
{
    public TimeSpan EffectiveStartupTimeout =>
        StartupTimeout > TimeSpan.Zero ? StartupTimeout : TimeSpan.FromSeconds(15);

    public TimeSpan EffectiveRecognitionTimeout =>
        RecognitionTimeout > TimeSpan.Zero ? RecognitionTimeout : TimeSpan.FromSeconds(20);
}

/// <summary>
/// 通过长驻隔离进程调用 RapidOCR。主进程只接收引擎无关的 OCR 契约，
/// Worker 异常或超时后会被终止，下一次识别重新拉起，以免污染 WPF UI 进程。
/// </summary>
public sealed class RapidOcrWorkerService : IOcrService, IOcrWarmupService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RapidOcrWorkerOptions _options;
    private readonly OcrResourceLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task? _errorDrain;
    private int _workerGeneration;
    private int _disposed;

    public RapidOcrWorkerService(
        RapidOcrWorkerOptions options,
        OcrResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PythonExecutable))
            throw new ArgumentException("Python 可执行文件路径不能为空。", nameof(options));
        if (string.IsNullOrWhiteSpace(options.WorkerScriptPath))
            throw new ArgumentException("OCR Worker 脚本路径不能为空。", nameof(options));

        _options = options;
        _limits = limits ?? OcrResourceLimits.Default;
    }

    public OcrCapability Probe()
    {
        if (!File.Exists(_options.PythonExecutable) ||
            !File.Exists(_options.WorkerScriptPath) ||
            !OptionalModelFilesExist())
        {
            return OcrCapability.Unavailable("本地场景 OCR Worker 未安装。");
        }

        return OcrCapability.Available(
            new[] { "multi" },
            _limits.MaxImageDimension,
            engineId: "rapidocr-onnx-worker",
            supportsPolygons: true,
            supportsConfidence: true);
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(image);
        image.Validate(_limits);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        timeoutCts.CancelAfter(_options.EffectiveRecognitionTimeout);
        var token = timeoutCts.Token;
        var acquired = false;
        var watch = Stopwatch.StartNew();
        var startupIncluded = false;

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            try
            {
                startupIncluded = await EnsureStartedAsync(token).ConfigureAwait(false);
                var requestId = Guid.NewGuid().ToString("N");
                var request = new WorkerRequest(
                    "recognize",
                    requestId,
                    image.PixelWidth,
                    image.PixelHeight,
                    image.PixelWidth * 4,
                    Convert.ToBase64String(PackRows(image)),
                    options?.LanguageHint);
                await SendAsync(request, token).ConfigureAwait(false);
                var response = await ReadResponseAsync(token).ConfigureAwait(false);
                if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    StopWorker("request_id_mismatch");
                    throw new OcrRecognitionException(
                        "本地场景 OCR Worker 协议请求身份不匹配。");
                }
                if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    StopWorker("worker_error_response");
                    throw new OcrRecognitionException(
                        $"本地场景 OCR Worker 识别失败（{response.ErrorType ?? "WorkerError"}）。");
                }

                var blocks = ConvertBlocks(response.Blocks, image.PixelWidth, image.PixelHeight);
                OcrBlockValidator.ValidateAll(blocks, image.PixelWidth, image.PixelHeight);
                watch.Stop();
                Logger.Info("Screenshot", "screenshot.ocr_worker_recognition_completed", new
                {
                    duration_ms = watch.Elapsed.TotalMilliseconds,
                    startup_included = startupIncluded,
                    block_count = blocks.Count,
                    worker_generation = _workerGeneration
                });
                return new(
                    blocks,
                    string.IsNullOrWhiteSpace(response.UsedLanguageTag)
                        ? "multi"
                        : response.UsedLanguageTag,
                    response.LanguageFallbackUsed,
                    response.TextAngleDegrees,
                    watch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_lifetimeCts.IsCancellationRequested)
                {
                    StopWorker("service_disposed");
                    throw;
                }

                StopWorker("recognition_timeout");
                throw new OcrRecognitionException("本地场景 OCR Worker 超时。");
            }
            catch (OperationCanceledException)
            {
                StopWorker("recognition_cancelled");
                throw;
            }
            catch (OcrRecognitionException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is IOException or InvalidOperationException or JsonException or ArgumentException)
            {
                StopWorker("protocol_error");
                throw new OcrRecognitionException(
                    $"本地场景 OCR Worker 通信失败（{ex.GetType().Name}）。", ex);
            }
        }
        catch (OperationCanceledException) when (
            !acquired &&
            !cancellationToken.IsCancellationRequested &&
            !_lifetimeCts.IsCancellationRequested)
        {
            throw new OcrRecognitionException("本地场景 OCR Worker 排队超时。");
        }
        finally
        {
            if (acquired)
                _gate.Release();
        }
    }

    /// <summary>幂等地启动 Worker 并等待模型初始化完成；失败后允许下一次调用重试。</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var acquired = false;
        var watch = Stopwatch.StartNew();
        try
        {
            await _gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            acquired = true;
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var started = await EnsureStartedAsync(linkedCts.Token).ConfigureAwait(false);
            watch.Stop();
            Logger.Info("Screenshot", "screenshot.ocr_worker_warmup_completed", new
            {
                duration_ms = watch.Elapsed.TotalMilliseconds,
                worker_started = started,
                worker_generation = _workerGeneration
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();
            Logger.Warn("Screenshot", "screenshot.ocr_worker_warmup_failed", new
            {
                duration_ms = watch.Elapsed.TotalMilliseconds,
                exception_type = ex.GetType().Name
            });
            throw;
        }
        finally
        {
            if (acquired)
                _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _gate.Wait();
        try
        {
            StopWorker("service_disposed");
        }
        finally
        {
            _gate.Release();
        }
        // Do not dispose synchronization primitives here. Calls that were already
        // queued before Dispose can still be completing their cancelled awaits.
        // The process and streams are the material resources and are closed above.
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _input is not null && _output is not null)
            return false;

        StopWorker("restart_before_start");
        if (!File.Exists(_options.PythonExecutable) || !File.Exists(_options.WorkerScriptPath))
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 未安装。");

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.WorkerScriptPath))
                ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(_options.WorkerScriptPath);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        SetOptionalEnvironment(startInfo, "QUICKTRANSLATE_OCR_MODEL_ID", _options.ModelId);
        SetOptionalEnvironment(startInfo, "QUICKTRANSLATE_OCR_DET_MODEL", _options.DetectionModelPath);
        SetOptionalEnvironment(startInfo, "QUICKTRANSLATE_OCR_REC_MODEL", _options.RecognitionModelPath);
        SetOptionalEnvironment(startInfo, "QUICKTRANSLATE_OCR_REC_KEYS", _options.RecognitionKeysPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startupWatch = Stopwatch.StartNew();
        var nextGeneration = _workerGeneration + 1;
        Logger.Info("Screenshot", "screenshot.ocr_worker_starting", new
        {
            worker_generation = nextGeneration,
            restart = _workerGeneration > 0
        });
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 OCR Worker。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new OcrEngineUnavailableException(
                $"本地场景 OCR Worker 启动失败（{ex.GetType().Name}）。", ex);
        }

        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _errorDrain = process.StandardError.ReadToEndAsync();

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(_options.EffectiveStartupTimeout);
        string? readyLine;
        try
        {
            readyLine = await _output.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopWorker("startup_timeout");
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 启动超时。");
        }
        catch (OperationCanceledException)
        {
            StopWorker("startup_cancelled");
            throw;
        }
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            StopWorker("startup_eof");
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 未返回就绪信号。");
        }

        WorkerResponse? ready;
        try
        {
            ready = JsonSerializer.Deserialize<WorkerResponse>(readyLine, JsonOptions);
        }
        catch (JsonException ex)
        {
            StopWorker("startup_invalid_json");
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 协议无效。", ex);
        }

        if (ready is null || !string.Equals(ready.Kind, "ready", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ready.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            StopWorker("startup_rejected");
            throw new OcrEngineUnavailableException(
                $"本地场景 OCR Worker 不可用（{ready?.ErrorType ?? "ReadyFailed"}）。");
        }
        if (!string.IsNullOrWhiteSpace(_options.ModelId) &&
            !string.Equals(ready.ModelFamily, _options.ModelId, StringComparison.Ordinal))
        {
            StopWorker("model_identity_mismatch");
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 未加载所选模型。");
        }

        _workerGeneration = nextGeneration;
        startupWatch.Stop();
        Logger.Info("Screenshot", "screenshot.ocr_worker_started", new
        {
            duration_ms = startupWatch.Elapsed.TotalMilliseconds,
            worker_generation = _workerGeneration,
            restart = _workerGeneration > 1
        });
        return true;
    }

    private bool OptionalModelFilesExist()
    {
        var paths = new[]
        {
            _options.DetectionModelPath,
            _options.RecognitionModelPath,
            _options.RecognitionKeysPath
        };
        var configuredCount = paths.Count(static path => !string.IsNullOrWhiteSpace(path));
        return configuredCount == 0 ||
               configuredCount == paths.Length && paths.All(static path => File.Exists(path));
    }

    private static void SetOptionalEnvironment(
        ProcessStartInfo startInfo,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            startInfo.Environment[name] = value;
    }

    private async Task SendAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (_input is null)
            throw new InvalidOperationException("OCR Worker 输入流未初始化。");
        await _input.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        if (_output is null)
            throw new InvalidOperationException("OCR Worker 输出流未初始化。");
        var line = await _output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("OCR Worker 已结束输出。");
        return JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions)
               ?? throw new JsonException("OCR Worker 返回空响应。");
    }

    private static byte[] PackRows(OcrImage image)
    {
        var rowBytes = checked(image.PixelWidth * 4);
        var packed = new byte[checked(rowBytes * image.PixelHeight)];
        var source = image.BgraPixels.Span;
        for (var row = 0; row < image.PixelHeight; row++)
        {
            source.Slice(row * image.Stride, rowBytes)
                .CopyTo(packed.AsSpan(row * rowBytes, rowBytes));
        }

        return packed;
    }

    private static IReadOnlyList<OcrTextBlock> ConvertBlocks(
        IReadOnlyList<WorkerBlock>? workerBlocks,
        int pixelWidth,
        int pixelHeight)
    {
        if (workerBlocks is null || workerBlocks.Count == 0)
            return Array.Empty<OcrTextBlock>();

        var blocks = new List<OcrTextBlock>(workerBlocks.Count);
        foreach (var workerBlock in workerBlocks)
        {
            if (workerBlock.Bounds is null)
                throw new JsonException("OCR Worker 块缺少 bounds。");
            var polygon = workerBlock.Polygon?.Select(static point => new OcrPoint(point.X, point.Y)).ToArray();
            blocks.Add(new OcrTextBlock(
                workerBlock.BlockId ?? string.Empty,
                workerBlock.Text ?? string.Empty,
                new OcrBounds(
                    workerBlock.Bounds.X,
                    workerBlock.Bounds.Y,
                    workerBlock.Bounds.Width,
                    workerBlock.Bounds.Height),
                workerBlock.Confidence,
                polygon,
                workerBlock.OrientationDegrees));
        }

        return blocks;
    }

    private void StopWorker(string reason)
    {
        var process = _process;
        var errorDrain = _errorDrain;
        _process = null;
        _input = null;
        _output = null;
        _errorDrain = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process may have exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cleanup must not mask the original OCR failure.
        }
        finally
        {
            process.Dispose();
        }

        if (errorDrain is not null)
            _ = ObserveErrorDrainAsync(errorDrain);
        Logger.Info("Screenshot", "screenshot.ocr_worker_stopped", new
        {
            reason,
            worker_generation = _workerGeneration
        });
    }

    private static async Task ObserveErrorDrainAsync(Task errorDrain)
    {
        try
        {
            await errorDrain.ConfigureAwait(false);
        }
        catch
        {
            // stderr content and stream cleanup failures are intentionally discarded.
        }
    }

    private sealed record WorkerRequest(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("request_id")] string RequestId,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("stride")] int Stride,
        [property: JsonPropertyName("bgra_base64")] string BgraBase64,
        [property: JsonPropertyName("language_hint")] string? LanguageHint);

    private sealed class WorkerResponse
    {
        [JsonPropertyName("request_id")] public string? RequestId { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("error_type")] public string? ErrorType { get; init; }
        [JsonPropertyName("model_family")] public string? ModelFamily { get; init; }
        [JsonPropertyName("used_language_tag")] public string? UsedLanguageTag { get; init; }
        [JsonPropertyName("language_fallback_used")] public bool LanguageFallbackUsed { get; init; }
        [JsonPropertyName("text_angle_degrees")] public double TextAngleDegrees { get; init; }
        [JsonPropertyName("blocks")] public List<WorkerBlock>? Blocks { get; init; }
    }

    private sealed class WorkerBlock
    {
        [JsonPropertyName("block_id")] public string? BlockId { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("confidence")] public double? Confidence { get; init; }
        [JsonPropertyName("orientation_degrees")] public double? OrientationDegrees { get; init; }
        [JsonPropertyName("polygon")] public List<WorkerPoint>? Polygon { get; init; }
        [JsonPropertyName("bounds")] public WorkerBounds? Bounds { get; init; }
    }

    private sealed class WorkerPoint
    {
        [JsonPropertyName("x")] public double X { get; init; }
        [JsonPropertyName("y")] public double Y { get; init; }
    }

    private sealed class WorkerBounds
    {
        [JsonPropertyName("x")] public int X { get; init; }
        [JsonPropertyName("y")] public int Y { get; init; }
        [JsonPropertyName("width")] public int Width { get; init; }
        [JsonPropertyName("height")] public int Height { get; init; }
    }
}
