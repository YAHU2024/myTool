using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class BigModelModelCapabilitiesTests
{
    [Theory]
    [InlineData("glm-5.2", true, true)]
    [InlineData("GLM-5.1", true, false)]
    [InlineData("glm-5v-turbo", true, false)]
    [InlineData("glm-4.7-flash", true, false)]
    [InlineData("glm-4.6v", true, false)]
    [InlineData("glm-4.5", true, false)]
    [InlineData("glm-4-flash", false, false)]
    [InlineData("unknown-model", false, false)]
    public void Resolve_UsesDocumentedModelFamilies(
        string modelName,
        bool supportsThinking,
        bool supportsReasoningEffort)
    {
        var capabilities = BigModelModelCapabilitiesResolver.Resolve(modelName);

        Assert.Equal(supportsThinking, capabilities.SupportsThinking);
        Assert.Equal(supportsReasoningEffort, capabilities.SupportedReasoningEfforts.Count > 0);
    }
}
