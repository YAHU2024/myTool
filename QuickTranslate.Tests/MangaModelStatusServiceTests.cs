using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MangaModelStatusServiceTests
{
    [Fact]
    public void Inspect_MissingDirectory()
    {
        var result = MangaModelStatusService.Inspect("manga", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "pytorch_model.bin");
        Assert.Equal(MangaModelInstallState.Missing, result.State);
    }

    [Fact]
    public void Inspect_RejectsEmptyFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try { File.WriteAllBytes(Path.Combine(dir.FullName, "model.bin"), Array.Empty<byte>()); Assert.Equal(MangaModelInstallState.Invalid, MangaModelStatusService.Inspect("m", dir.FullName, "model.bin").State); }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Inspect_AcceptsRequiredFiles()
    {
        var dir = Directory.CreateTempSubdirectory();
        try { File.WriteAllBytes(Path.Combine(dir.FullName, "model.bin"), new byte[] { 1 }); var result = MangaModelStatusService.Inspect("m", dir.FullName, "model.bin"); Assert.Equal(MangaModelInstallState.Installed, result.State); Assert.Equal(1, result.TotalBytes); }
        finally { dir.Delete(true); }
    }
}
