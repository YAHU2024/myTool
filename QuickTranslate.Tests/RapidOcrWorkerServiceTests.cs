using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class RapidOcrWorkerServiceTests
{
    [Fact]
    public void Probe_ReturnsUnavailable_WhenWorkerFilesAreMissing()
    {
        using var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "python.exe"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ocr-worker.py")));

        var capability = service.Probe();

        Assert.False(capability.IsAvailable);
        Assert.False(capability.SupportsPolygons);
        Assert.False(capability.SupportsConfidence);
    }

    [Fact]
    public async Task RecognizeAsync_ValidatesImageBeforeStartingWorker()
    {
        using var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            "missing-python.exe",
            "missing-worker.py"));
        var image = new OcrImage(10, 10, 39, new byte[400]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeAsync(image));
    }

    [Fact]
    public async Task RecognizeAsync_RejectsCallsAfterDispose()
    {
        var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            "missing-python.exe",
            "missing-worker.py"));
        service.Dispose();
        var image = new OcrImage(1, 1, 4, new byte[4]);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RecognizeAsync(image));
    }

    [Fact]
    public async Task WarmUpAsync_ReturnsUnavailable_WhenWorkerFilesAreMissing()
    {
        using var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            "missing-python.exe", "missing-worker.py"));

        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => service.WarmUpAsync());
    }

    [Fact]
    public async Task WarmUpAsync_HonorsCancellationBeforeStarting()
    {
        using var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            "missing-python.exe", "missing-worker.py"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WarmUpAsync(cancellation.Token));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new RapidOcrWorkerService(new RapidOcrWorkerOptions(
            "missing-python.exe", "missing-worker.py"));

        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public async Task WarmUpAsync_IsIdempotent_AndRecognitionUsesWarmWorker()
    {
        using var worker = FakeWorker.Create("success");
        using var service = worker.CreateService();

        await service.WarmUpAsync();
        await service.WarmUpAsync();
        var result = await service.RecognizeAsync(CreateImage());

        Assert.Single(result.Blocks);
        Assert.Equal(1, worker.ReadStartCount());
    }

    [Fact]
    public async Task RecognizeAsync_MismatchedRequestIdInvalidatesWorker()
    {
        using var worker = FakeWorker.Create("mismatch_once");
        using var service = worker.CreateService();

        await Assert.ThrowsAsync<OcrRecognitionException>(() => service.RecognizeAsync(CreateImage()));
        var result = await service.RecognizeAsync(CreateImage());

        Assert.Single(result.Blocks);
        Assert.Equal(2, worker.ReadStartCount());
    }

    [Fact]
    public async Task RecognizeAsync_ExitAtResponseIsReportedAsCommunicationFailure()
    {
        using var worker = FakeWorker.Create("exit");
        using var service = worker.CreateService();

        await Assert.ThrowsAsync<OcrRecognitionException>(() => service.RecognizeAsync(CreateImage()));
    }

    [Fact]
    public async Task WarmUpAsync_InvalidReadyJsonInvalidatesWorker()
    {
        using var worker = FakeWorker.Create("invalid_ready");
        using var service = worker.CreateService();

        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => service.WarmUpAsync());
    }

    [Fact]
    public async Task WarmUpAsync_StartupTimeoutInvalidatesWorker()
    {
        using var worker = FakeWorker.Create("slow_ready");
        using var service = worker.CreateService(startupTimeout: TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => service.WarmUpAsync());
    }

    [Fact]
    public async Task RecognizeAsync_UserCancellationStopsWorker()
    {
        using var worker = FakeWorker.Create("slow");
        using var service = worker.CreateService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RecognizeAsync(CreateImage(), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Dispose_CancelsActiveRecognitionWithoutDeadlock()
    {
        using var worker = FakeWorker.Create("slow");
        var service = worker.CreateService();
        var recognition = service.RecognizeAsync(CreateImage());
        await worker.WaitForRequestAsync();

        await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognition);
    }

    private static OcrImage CreateImage() => new(1, 1, 4, new byte[4]);

    private sealed class FakeWorker : IDisposable
    {
        private readonly string _directory;
        private readonly string _counterPath;
        private readonly string _requestPath;

        private FakeWorker(string directory, string scriptPath)
        {
            _directory = directory;
            ScriptPath = scriptPath;
            _counterPath = Path.Combine(directory, "starts.txt");
            _requestPath = Path.Combine(directory, "request.txt");
        }

        public string ScriptPath { get; }

        public static FakeWorker Create(string mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), "quicktranslate-worker-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var scriptPath = Path.Combine(directory, "worker.ps1");
            var counter = Path.Combine(directory, "starts.txt").Replace("'", "''");
            var request = Path.Combine(directory, "request.txt").Replace("'", "''");
            var script = $$"""
                $counter = '{{counter}}'
                $requestMarker = '{{request}}'
                $count = if (Test-Path -LiteralPath $counter) { [int](Get-Content -LiteralPath $counter -Raw) } else { 0 }
                Set-Content -LiteralPath $counter -Value ($count + 1) -NoNewline
                if ('{{mode}}' -eq 'slow_ready') { Start-Sleep -Seconds 5 }
                if ('{{mode}}' -eq 'invalid_ready') {
                    [Console]::Out.WriteLine('not-json')
                    [Console]::Out.Flush()
                    exit 8
                }
                [Console]::Out.WriteLine('{"request_id":null,"kind":"ready","status":"ok"}')
                [Console]::Out.Flush()
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                    Set-Content -LiteralPath $requestMarker -Value 'seen' -NoNewline
                    $message = $line | ConvertFrom-Json
                    if ('{{mode}}' -eq 'exit') { exit 7 }
                    if ('{{mode}}' -eq 'slow') { Start-Sleep -Seconds 5 }
                    $id = if ('{{mode}}' -eq 'mismatch_once' -and $count -eq 0) { 'wrong-id' } else { $message.request_id }
                    $payload = @{ request_id = $id; status = 'ok'; used_language_tag = 'multi'; blocks = @(@{ block_id = 'b1'; text = 'ok'; confidence = 1; bounds = @{ x = 0; y = 0; width = 1; height = 1 } }) } | ConvertTo-Json -Compress -Depth 5
                    [Console]::Out.WriteLine($payload)
                    [Console]::Out.Flush()
                }
                """;
            File.WriteAllText(scriptPath, script);
            return new FakeWorker(directory, scriptPath);
        }

        public RapidOcrWorkerService CreateService(TimeSpan? startupTimeout = null)
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return new RapidOcrWorkerService(new RapidOcrWorkerOptions(
                powershell, ScriptPath, startupTimeout ?? TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
        }

        public int ReadStartCount() => int.Parse(File.ReadAllText(_counterPath));

        public async Task WaitForRequestAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(_requestPath))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Fake Worker did not receive a request.");
                await Task.Delay(20);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
