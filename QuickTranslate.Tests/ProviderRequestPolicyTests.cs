using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ProviderRequestPolicyTests
{
    [Fact]
    public void ResolveCapabilities_DescribesDeepSeekThinkingContract()
    {
        var capabilities = ProviderRequestPolicy.ResolveCapabilities(
            "https://api.deepseek.com/v1",
            "deepseek-v4-pro");

        Assert.Equal(ThinkingParameterStyle.ThinkingObject, capabilities.ThinkingStyle);
        Assert.Equal(["low", "high", "xhigh", "max"], capabilities.SupportedReasoningEfforts);
        Assert.True(capabilities.OmitSamplingParametersWhenThinking);
        Assert.True(capabilities.ReturnsReasoningContent);
        Assert.True(capabilities.RequiresReasoningContentForToolContinuation);
    }

    [Fact]
    public void ResolveCapabilities_UsesConservativeFallbackForUnknownDeepSeekModel()
    {
        var capabilities = ProviderRequestPolicy.ResolveCapabilities(
            "https://api.deepseek.com/v1",
            "deepseek-chat");

        Assert.False(capabilities.SupportsThinking);
    }

    [Fact]
    public void Apply_RemovesIgnoredSamplingParametersWhenDeepSeekThinkingIsEnabled()
    {
        var body = new Dictionary<string, object>
        {
            ["temperature"] = 0.3,
            ["top_p"] = 0.8,
            ["presence_penalty"] = 0.1,
            ["frequency_penalty"] = 0.2
        };

        ProviderRequestPolicy.Apply(
            body,
            "https://api.deepseek.com/v1",
            "deepseek-v4-flash",
            enableThinking: true);

        Assert.Equal("enabled", GetThinkingType(body));
        Assert.DoesNotContain("temperature", body.Keys);
        Assert.DoesNotContain("top_p", body.Keys);
        Assert.DoesNotContain("presence_penalty", body.Keys);
        Assert.DoesNotContain("frequency_penalty", body.Keys);
    }

    [Fact]
    public void Apply_KeepsSamplingParametersWhenDeepSeekThinkingIsDisabled()
    {
        var body = new Dictionary<string, object> { ["temperature"] = 0.3 };

        ProviderRequestPolicy.Apply(
            body,
            "https://api.deepseek.com/v1",
            "deepseek-v4-flash",
            enableThinking: false);

        Assert.Equal("disabled", GetThinkingType(body));
        Assert.Contains("temperature", body.Keys);
    }

    private static string? GetThinkingType(Dictionary<string, object> body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body["thinking"]);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("type").GetString();
    }
}
