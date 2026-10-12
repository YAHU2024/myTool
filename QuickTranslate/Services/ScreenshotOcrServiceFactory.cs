using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>按安装状态选择本地场景 OCR，找不到模型运行时则安全回退到 Windows OCR。</summary>
public static class ScreenshotOcrServiceFactory
{
    public static IOcrService Create(
        AppSettings? settings = null,
        OcrModelManager? modelManager = null,
        OcrResourceLimits? limits = null,
        string? baseDirectory = null,
        IDictionary<string, string?>? environment = null)
    {
        if (!string.Equals(settings?.ScreenshotOcrEngine, "rapidocr", StringComparison.OrdinalIgnoreCase))
            return new WindowsMediaOcrService(limits);

        var model = OcrModelCatalog.Find(settings!.ScreenshotOcrModelId);
        var status = model is null || modelManager is null
            ? null
            : modelManager.GetStatus(model);
        if (model is null || status is not { State: OcrModelInstallState.Installed, InstallDirectory: not null })
            return new WindowsMediaOcrService(limits);

        var rapidOcr = CreateRapidOcr(model, status.InstallDirectory, limits, baseDirectory, environment);
        return rapidOcr is null ? new WindowsMediaOcrService(limits) : rapidOcr;
    }

    public static RapidOcrWorkerService? CreateRapidOcr(
        OcrModelDescriptor model,
        string installDirectory,
        OcrResourceLimits? limits = null,
        string? baseDirectory = null,
        IDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(installDirectory))
            throw new ArgumentException("OCR 模型安装目录不能为空。", nameof(installDirectory));

        var runtime = ResolveRapidOcrRuntime(baseDirectory, environment);
        return runtime is null
            ? null
            : CreateWorker(model, installDirectory, runtime.Value.Python, runtime.Value.Worker, limits);
    }

    public static bool IsRapidOcrRuntimeAvailable(
        string? baseDirectory = null,
        IDictionary<string, string?>? environment = null) =>
        ResolveRapidOcrRuntime(baseDirectory, environment) is not null;

    private static RapidOcrWorkerService CreateWorker(
        OcrModelDescriptor model,
        string installDirectory,
        string python,
        string worker,
        OcrResourceLimits? limits) => new(
            new RapidOcrWorkerOptions(
                python,
                worker,
                ModelId: model.Id,
                DetectionModelPath: Path.Combine(installDirectory, model.DetModelRelativePath),
                RecognitionModelPath: Path.Combine(installDirectory, model.RecModelRelativePath),
                RecognitionKeysPath: Path.Combine(installDirectory, model.RecKeysRelativePath)),
            limits);

    private static string? GetValue(
        IDictionary<string, string?> environment,
        string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static (string Python, string Worker)? ResolveRapidOcrRuntime(
        string? baseDirectory,
        IDictionary<string, string?>? environment)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var variables = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                static entry => (string)entry.Key,
                static entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);
        var python = GetValue(variables, "QUICKTRANSLATE_OCR_PYTHON");
        var worker = GetValue(variables, "QUICKTRANSLATE_OCR_WORKER");
        if (python is not null && worker is not null && File.Exists(python) && File.Exists(worker))
            return (python, worker);

        foreach (var runtimeRoot in EnumerateRuntimeRoots(root))
        {
            var bundledPython = ResolveBundledPython(runtimeRoot);
            var bundledWorker = Path.Combine(runtimeRoot, "ocr-worker.py");
            if (File.Exists(bundledPython) && File.Exists(bundledWorker))
                return (bundledPython, bundledWorker);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRuntimeRoots(string baseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            var runtimeRoot = Path.Combine(current.FullName, "ocr-runtime");
            if (seen.Add(runtimeRoot))
                yield return runtimeRoot;
        }

        var workingRoot = Path.Combine(Environment.CurrentDirectory, "ocr-runtime");
        if (seen.Add(workingRoot))
            yield return workingRoot;
    }

    private static string ResolveBundledPython(string runtimeRoot)
    {
        var embedded = Path.Combine(runtimeRoot, "python.exe");
        return File.Exists(embedded)
            ? embedded
            : Path.Combine(runtimeRoot, "Scripts", "python.exe");
    }
}
