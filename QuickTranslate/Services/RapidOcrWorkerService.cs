using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslate.Core;
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
    TimeSpan RecognitionTimeout = default)
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
public sealed class RapidOcrWorkerService : IOcrService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RapidOcrWorkerOptions _options;
    private readonly OcrResourceLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task? _errorDrain;
    private bool _disposed;

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
        if (!File.Exists(_options.PythonExecutable) || !File.Exists(_options.WorkerScriptPath))
        {
            return OcrCapability.Unavailable("本地场景 OCR Worker 未安装。");
        }

        return OcrCapability.Available(
            new[] { "und" },
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        image.Validate(_limits);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.EffectiveRecognitionTimeout);
        var token = timeoutCts.Token;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await EnsureStartedAsync(token).ConfigureAwait(false);
                var request = new WorkerRequest(
                    "recognize",
                    Guid.NewGuid().ToString("N"),
                    image.PixelWidth,
                    image.PixelHeight,
                    image.PixelWidth * 4,
                    Convert.ToBase64String(PackRows(image)),
                    options?.LanguageHint);
                await SendAsync(request, token).ConfigureAwait(false);
                var response = await ReadResponseAsync(token).ConfigureAwait(false);
                if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    StopWorker();
                    throw new OcrRecognitionException(
                        $"本地场景 OCR Worker 识别失败（{response.ErrorType ?? "WorkerError"}）。");
                }

                var blocks = ConvertBlocks(response.Blocks, image.PixelWidth, image.PixelHeight);
                OcrBlockValidator.ValidateAll(blocks, image.PixelWidth, image.PixelHeight);
                watch.Stop();
                return new(
                    blocks,
                    string.IsNullOrWhiteSpace(response.UsedLanguageTag)
                        ? "und"
                        : response.UsedLanguageTag,
                    response.LanguageFallbackUsed,
                    response.TextAngleDegrees,
                    watch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopWorker();
                throw new OcrRecognitionException("本地场景 OCR Worker 超时。");
            }
            catch (OperationCanceledException)
            {
                StopWorker();
                throw;
            }
            catch (OcrRecognitionException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            {
                StopWorker();
                throw new OcrRecognitionException(
                    $"本地场景 OCR Worker 通信失败（{ex.GetType().Name}）。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopWorker();
        _gate.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _input is not null && _output is not null)
            return;

        StopWorker();
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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
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
        var readyLine = await _output.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            StopWorker();
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 未返回就绪信号。");
        }

        WorkerResponse? ready;
        try
        {
            ready = JsonSerializer.Deserialize<WorkerResponse>(readyLine, JsonOptions);
        }
        catch (JsonException ex)
        {
            StopWorker();
            throw new OcrEngineUnavailableException("本地场景 OCR Worker 协议无效。", ex);
        }

        if (ready is null || !string.Equals(ready.Kind, "ready", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ready.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            StopWorker();
            throw new OcrEngineUnavailableException(
                $"本地场景 OCR Worker 不可用（{ready?.ErrorType ?? "ReadyFailed"}）。");
        }
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

    private void StopWorker()
    {
        var process = _process;
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
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("error_type")] public string? ErrorType { get; init; }
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
