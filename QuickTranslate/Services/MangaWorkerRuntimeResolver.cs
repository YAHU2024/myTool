using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record MangaWorkerRuntime(string PythonPath, string ScriptPath, bool IsBundled);

public static class MangaWorkerRuntimeResolver
{
    public static MangaWorkerRuntime? Resolve(AppSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuredPython = settings.MangaWorkerPythonPath;
        var configuredScript = settings.MangaWorkerScriptPath;
        if (File.Exists(configuredPython) && File.Exists(configuredScript))
            return new(Path.GetFullPath(configuredPython), Path.GetFullPath(configuredScript), false);

        var bundledPython = Path.Combine(baseDirectory, "ocr-runtime", "Scripts", "python.exe");
        var bundledScript = Path.Combine(baseDirectory, "scripts", "manga-worker.py");
        return File.Exists(bundledPython) && File.Exists(bundledScript)
            ? new(bundledPython, bundledScript, true)
            : null;
    }
}
