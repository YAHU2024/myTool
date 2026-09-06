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
}
