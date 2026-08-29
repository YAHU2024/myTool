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
}
