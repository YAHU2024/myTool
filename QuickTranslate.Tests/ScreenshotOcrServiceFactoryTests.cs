using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotOcrServiceFactoryTests
{
    [Fact]
    public void Create_UsesWindowsFallback_WhenWorkerIsNotInstalled()
    {
        var service = ScreenshotOcrServiceFactory.Create(
            baseDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            environment: new Dictionary<string, string?>());

        Assert.IsType<WindowsMediaOcrService>(service);
        (service as IDisposable)?.Dispose();
    }

    [Fact]
    public void Create_UsesWorker_WhenBothConfiguredFilesExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var python = Path.Combine(directory, "python.exe");
        var worker = Path.Combine(directory, "worker.py");
        File.WriteAllBytes(python, Array.Empty<byte>());
        File.WriteAllText(worker, string.Empty);
        try
        {
            var service = ScreenshotOcrServiceFactory.Create(
                environment: new Dictionary<string, string?>
                {
                    ["QUICKTRANSLATE_OCR_PYTHON"] = python,
                    ["QUICKTRANSLATE_OCR_WORKER"] = worker
                });

            Assert.IsType<RapidOcrWorkerService>(service);
            (service as IDisposable)?.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
