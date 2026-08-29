using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>按安装状态选择本地场景 OCR，找不到模型运行时则安全回退到 Windows OCR。</summary>
public static class ScreenshotOcrServiceFactory
{
    public static IOcrService Create(
        OcrResourceLimits? limits = null,
        string? baseDirectory = null,
        IDictionary<string, string?>? environment = null)
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
        {
            return new RapidOcrWorkerService(
                new RapidOcrWorkerOptions(python, worker),
                limits);
        }

        foreach (var runtimeRoot in EnumerateRuntimeRoots(root))
        {
            var bundledPython = ResolveBundledPython(runtimeRoot);
            var bundledWorker = Path.Combine(runtimeRoot, "ocr-worker.py");
            if (File.Exists(bundledPython) && File.Exists(bundledWorker))
            {
                return new RapidOcrWorkerService(
                    new RapidOcrWorkerOptions(bundledPython, bundledWorker),
                    limits);
            }
        }

        return new WindowsMediaOcrService(limits);
    }

    private static string? GetValue(
        IDictionary<string, string?> environment,
        string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

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
