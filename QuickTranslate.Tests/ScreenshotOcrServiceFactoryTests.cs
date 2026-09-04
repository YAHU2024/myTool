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
    public void CreateRapidOcr_UsesWorker_WhenModelFilesAndRuntimeExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var python = Path.Combine(directory, "python.exe");
        var worker = Path.Combine(directory, "worker.py");
        var det = Path.Combine(directory, "det.onnx");
        var rec = Path.Combine(directory, "rec.onnx");
        var keys = Path.Combine(directory, "dict.txt");
        File.WriteAllBytes(python, Array.Empty<byte>());
        File.WriteAllText(worker, string.Empty);
        File.WriteAllBytes(det, Array.Empty<byte>());
        File.WriteAllBytes(rec, Array.Empty<byte>());
        File.WriteAllText(keys, "x");
        var model = new OcrModelDescriptor(
            "test-model", "1", "Test", "Test", "Apache-2.0", 0,
            new[]
            {
                new OcrModelArtifact("det/inference.onnx", 0, "", new Uri("https://example.invalid/det")),
                new OcrModelArtifact("rec/inference.onnx", 0, "", new Uri("https://example.invalid/rec")),
                new OcrModelArtifact("rec/ppocrv6_dict.txt", 1, "", new Uri("https://example.invalid/dict"))
            });
        try
        {
            var service = ScreenshotOcrServiceFactory.CreateRapidOcr(
                model,
                directory,
                baseDirectory: directory,
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

    [Fact]
    public void Create_UsesWindowsFallback_WhenRapidModelIsNotInstalled()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var manager = new OcrModelManager(root, new HttpClientHandler());
        var settings = new QuickTranslate.Models.AppSettings
        {
            ScreenshotOcrEngine = "rapidocr",
            ScreenshotOcrModelId = OcrModelCatalog.DefaultModelId
        };

        var service = ScreenshotOcrServiceFactory.Create(
            settings,
            manager,
            baseDirectory: root,
            environment: new Dictionary<string, string?>());

        Assert.IsType<WindowsMediaOcrService>(service);
        (service as IDisposable)?.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
