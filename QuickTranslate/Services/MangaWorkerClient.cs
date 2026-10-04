using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QuickTranslate.Services;

public sealed record MangaWorkerProgress(string RequestId, string Stage, int Completed, int Total);

public sealed record MangaWorkerResponse(
    string RequestId,
    string Type,
    string? Stage,
    JsonElement Payload);

public static class MangaWorkerEvidenceParser
{
    public static ScreenshotSceneEvidence ToSceneEvidence(MangaWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(response.Type, "completed", StringComparison.Ordinal) ||
            !response.Payload.TryGetProperty("blocks", out var blocks) ||
            blocks.ValueKind != JsonValueKind.Array)
            return new(0, 0, 0, false);

        var total = 0;
        var bubbles = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            total++;
            if (block.TryGetProperty("region_type", out var region) &&
                string.Equals(region.GetString(), "text_bubble", StringComparison.OrdinalIgnoreCase))
                bubbles++;
        }
        var score = total == 0 ? 0 : Math.Clamp((double)bubbles / total, 0, 1);
        return new(total, bubbles, score, true);
    }
}

/// <summary>Runs the isolated manga worker as a one-request process.</summary>
public sealed class MangaWorkerClient
{
    private static readonly TimeSpan ProcessExitWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly string _pythonPath;
    private readonly string _workerPath;

    public MangaWorkerClient(string pythonPath, string workerPath)
    {
        _pythonPath = pythonPath ?? throw new ArgumentNullException(nameof(pythonPath));
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
    }

    public async Task<MangaWorkerResponse> RunAsync(
        object request,
        TimeSpan timeout,
        Action<MangaWorkerProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var requestJson = JsonSerializer.Serialize(request);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                WorkingDirectory = Path.GetDirectoryName(_workerPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add(_workerPath);

        if (!process.Start())
            throw new InvalidOperationException("MangaWorkerStartFailed");

        await process.StandardInput.WriteLineAsync(requestJson).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                    throw new InvalidOperationException("MangaWorkerUnexpectedEof");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString() ?? string.Empty;
                var requestId = root.GetProperty("request_id").GetString() ?? string.Empty;
                if (type == "progress")
                {
                    onProgress?.Invoke(new MangaWorkerProgress(requestId, root.GetProperty("stage").GetString() ?? string.Empty,
                        root.GetProperty("completed").GetInt32(), root.GetProperty("total").GetInt32()));
                    continue;
                }

                var payload = root.Clone();
                if (type == "completed")
                    return new MangaWorkerResponse(requestId, type, null, payload);
                if (type is "failed" or "cancelled")
                    return new MangaWorkerResponse(requestId, type, root.TryGetProperty("stage", out var stage) ? stage.GetString() : null, payload);
                throw new InvalidOperationException("MangaWorkerUnknownResponse");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(ProcessExitWaitTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Cancellation must release the screenshot entry point even
                // when a misbehaving child process ignores the kill request.
            }
        }
    }

    private static void TryKill(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
