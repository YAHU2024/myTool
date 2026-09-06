using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record MangaWorkerRuntime(string PythonPath, string ScriptPath, bool IsBundled);

public static class MangaWorkerRuntimeResolver
{
    public static MangaWorkerRuntime? Resolve(AppSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuredPython = settings.MangaWorkerPythonPath?.Trim();
        var configuredScript = settings.MangaWorkerScriptPath?.Trim();
        if (File.Exists(configuredPython) && File.Exists(configuredScript))
            return new(Path.GetFullPath(configuredPython), Path.GetFullPath(configuredScript), false);

        // Development checkouts keep the Comic Translate source and its heavier
        // dependencies in the ignored spike environment. Prefer that pair over
        // the lightweight OCR runtime, which intentionally does not include the
        // manga worker's optional packages such as mahotas and torch.
        foreach (var root in EnumerateRoots(baseDirectory))
        {
            var developmentPython = Path.Combine(
                root,
                ".m4-external-spike",
                "comic-translate",
                ".venv",
                "Scripts",
                "python.exe");
            var developmentScript = Path.Combine(root, "scripts", "manga-worker.py");
            if (File.Exists(developmentPython) && File.Exists(developmentScript))
                return new(Path.GetFullPath(developmentPython), Path.GetFullPath(developmentScript), false);

            var bundledPython = ResolveBundledPython(Path.Combine(root, "ocr-runtime"));
            var bundledScript = Path.Combine(root, "scripts", "manga-worker.py");
            if (File.Exists(bundledPython) && File.Exists(bundledScript))
                return new(Path.GetFullPath(bundledPython), Path.GetFullPath(bundledScript), true);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRoots(string baseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            if (seen.Add(current.FullName))
                yield return current.FullName;
        }

        var workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (seen.Add(workingDirectory))
            yield return workingDirectory;
    }

    private static string ResolveBundledPython(string runtimeRoot)
    {
        var embedded = Path.Combine(runtimeRoot, "python.exe");
        return File.Exists(embedded)
            ? embedded
            : Path.Combine(runtimeRoot, "Scripts", "python.exe");
    }
}
