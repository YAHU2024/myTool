using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ProviderPresetCatalogTests
{
    [Fact]
    public void Default_UsesDocumentedSiliconFlowPreset()
    {
        Assert.Equal("硅基流动", ProviderPresetCatalog.Default.DisplayName);
        Assert.Equal("https://api.siliconflow.cn/v1", ProviderPresetCatalog.Default.ApiBaseUrl);
        Assert.Equal("Qwen/Qwen3-8B", ProviderPresetCatalog.Default.ModelName);
    }

    [Fact]
    public void All_ContainsSupportedProvidersWithoutCredentials()
    {
        Assert.Collection(
            ProviderPresetCatalog.All,
            preset => AssertPreset(preset, "硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen3-8B"),
            preset => AssertPreset(preset, "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash"),
            preset => AssertPreset(preset, "DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash"),
            preset => AssertPreset(preset, "OpenAI", "https://api.openai.com/v1", "gpt-5.4"));

        Assert.DoesNotContain(
            ProviderPresetCatalog.All,
            preset => preset.GetType().GetProperties().Any(property =>
                property.Name.Contains("key", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertPreset(
        ProviderPreset preset,
        string displayName,
        string apiBaseUrl,
        string modelName)
    {
        Assert.Equal(displayName, preset.DisplayName);
        Assert.Equal(apiBaseUrl, preset.ApiBaseUrl);
        Assert.Equal(modelName, preset.ModelName);
    }
}
