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
        python ??= Path.Combine(root, "ocr-runtime", "python.exe");
        worker ??= Path.Combine(root, "ocr-runtime", "ocr-worker.py");

        if (File.Exists(python) && File.Exists(worker))
        {
            return new RapidOcrWorkerService(
                new RapidOcrWorkerOptions(python, worker),
                limits);
        }

        return new WindowsMediaOcrService(limits);
    }

    private static string? GetValue(
        IDictionary<string, string?> environment,
        string name) =>
        environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
