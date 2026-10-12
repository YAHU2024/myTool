using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MangaWorkerRuntimeResolverTests
{
    [Fact]
    public void Resolve_MissingRuntimeReturnsNull()
    {
        var settings = new AppSettings { MangaWorkerPythonPath = "missing-python", MangaWorkerScriptPath = "missing-script" };
        Assert.Null(MangaWorkerRuntimeResolver.Resolve(settings, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    }

    [Fact]
    public void Resolve_FindsComicTranslateDevelopmentRuntimeFromNestedOutputDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var baseDirectory = Path.Combine(root, "QuickTranslate", "bin", "Debug", "net8.0-windows");
        var python = Path.Combine(root, ".m4-external-spike", "comic-translate", ".venv", "Scripts", "python.exe");
        var script = Path.Combine(root, "scripts", "manga-worker.py");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllBytes(python, Array.Empty<byte>());
        File.WriteAllText(script, string.Empty);

        try
        {
            var runtime = MangaWorkerRuntimeResolver.Resolve(new AppSettings(), baseDirectory);
            Assert.NotNull(runtime);
            Assert.Equal(Path.GetFullPath(python), runtime!.PythonPath);
            Assert.Equal(Path.GetFullPath(script), runtime.ScriptPath);
            Assert.False(runtime.IsBundled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PrefersConfiguredPair()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var python = Path.Combine(root, "python.exe");
        var script = Path.Combine(root, "worker.py");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(python, Array.Empty<byte>());
        File.WriteAllText(script, string.Empty);

        try
        {
            var runtime = MangaWorkerRuntimeResolver.Resolve(
                new AppSettings { MangaWorkerPythonPath = python, MangaWorkerScriptPath = script },
                Path.Combine(root, "missing-output"));
            Assert.NotNull(runtime);
            Assert.Equal(Path.GetFullPath(python), runtime!.PythonPath);
            Assert.False(runtime.IsBundled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
