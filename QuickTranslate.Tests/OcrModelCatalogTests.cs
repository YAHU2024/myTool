using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrModelCatalogTests
{
    [Fact]
    public void Catalog_UsesPinnedOfficialHuggingFaceArtifacts()
    {
        Assert.Equal(2, OcrModelCatalog.All.Count);
        Assert.NotNull(OcrModelCatalog.Find(OcrModelCatalog.DefaultModelId));

        foreach (var model in OcrModelCatalog.All)
        {
            Assert.EndsWith("-cpu", model.Id, StringComparison.Ordinal);
            Assert.Equal("Apache-2.0（官方模型卡声明）", model.License);
            Assert.Equal(model.TotalSizeBytes, model.Artifacts.Sum(artifact => artifact.SizeBytes));
            Assert.All(model.Artifacts, artifact =>
            {
                Assert.Equal("huggingface.co", artifact.DownloadUri.Host);
                Assert.StartsWith("/PaddlePaddle/", artifact.DownloadUri.AbsolutePath, StringComparison.Ordinal);
                Assert.Matches(@"/resolve/[0-9a-f]{40}/", artifact.DownloadUri.AbsolutePath);
                Assert.DoesNotContain("/main/", artifact.DownloadUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                Assert.Matches("^[0-9A-F]{64}$", artifact.Sha256);
                Assert.True(artifact.SizeBytes > 0);
                Assert.DoesNotContain("..", artifact.RelativePath, StringComparison.Ordinal);
            });
        }
    }
}
