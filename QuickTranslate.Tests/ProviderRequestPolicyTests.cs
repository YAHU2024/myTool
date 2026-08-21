using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ProviderRequestPolicyTests
{
    [Theory]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "BigModel")]
    [InlineData("https://api.deepseek.com/v1", "DeepSeek")]
    [InlineData("https://api.siliconflow.cn/v1", "SiliconFlow")]
    [InlineData("https://api.openai.com/v1", "OpenAI")]
    [InlineData("https://api.openai.com.evil.example/v1", "Unknown")]
    [InlineData("https://chat.openai.com/v1", "Unknown")]
    [InlineData("not-a-url", "Unknown")]
    public void ResolveProvider_UsesNormalizedHost(string apiBaseUrl, string expected)
    {
        Assert.Equal(expected, ProviderEndpointResolver.Resolve(apiBaseUrl).ToString());
    }

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
        Assert.Equal(ThinkingControlAvailability.Unknown, capabilities.ThinkingControlAvailability);
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

    [Fact]
    public void Apply_FollowProviderDefault_OmitsThinkingParameters()
    {
        var body = new Dictionary<string, object> { ["temperature"] = 0.3 };

        ProviderRequestPolicy.Apply(
            body,
            "https://api.openai.com/v1",
            "gpt-5.4",
            enableThinking: null);

        Assert.DoesNotContain("reasoning_effort", body.Keys);
        Assert.Contains("temperature", body.Keys);
    }

    [Theory]
    [InlineData(ThinkingModePreference.FollowProviderDefault, null)]
    [InlineData(ThinkingModePreference.Enabled, true)]
    [InlineData(ThinkingModePreference.Disabled, false)]
    public void ResolveThinkingRequestValue_MapsControllablePreference(
        ThinkingModePreference preference,
        bool? expected)
    {
        Assert.Equal(
            expected,
            ProviderRequestPolicy.ResolveThinkingRequestValue(
                "https://api.openai.com/v1",
                "gpt-5.4",
                preference));
    }

    [Theory]
    [InlineData(ThinkingModePreference.FollowProviderDefault)]
    [InlineData(ThinkingModePreference.Enabled)]
    [InlineData(ThinkingModePreference.Disabled)]
    public void ResolveThinkingRequestValue_UnknownModelFallsBackToProviderDefault(
        ThinkingModePreference preference)
    {
        Assert.Null(ProviderRequestPolicy.ResolveThinkingRequestValue(
            "https://compatible.example.com/v1",
            "default-thinking-model",
            preference));
    }

    [Fact]
    public void ResolveCapabilities_DescribesCurrentOpenAIReasoningModels()
    {
        var capabilities = ProviderRequestPolicy.ResolveCapabilities(
            "https://api.openai.com/v1",
            "gpt-5.6");

        Assert.Equal(ThinkingParameterStyle.ReasoningEffort, capabilities.ThinkingStyle);
        Assert.Equal(["none", "low", "medium", "high", "xhigh", "max"], capabilities.SupportedReasoningEfforts);
        Assert.Equal("medium", capabilities.EnabledReasoningEffort);
        Assert.Equal("none", capabilities.DisabledReasoningEffort);
        Assert.True(capabilities.OmitSamplingParametersWhenThinking);
        Assert.Equal(ThinkingControlAvailability.Controllable, capabilities.ThinkingControlAvailability);
        Assert.True(capabilities.CanEnableThinking);
        Assert.True(capabilities.CanDisableThinking);
    }

    [Theory]
    [InlineData("gpt-5.2")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("gpt-5.5")]
    public void ResolveCapabilities_SupportsVerifiedOpenAIReasoningFamilies(string modelName)
    {
        var capabilities = ProviderRequestPolicy.ResolveCapabilities(
            "https://api.openai.com/v1",
            modelName);

        Assert.True(capabilities.SupportsThinking);
        Assert.DoesNotContain("max", capabilities.SupportedReasoningEfforts);
    }

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("o3")]
    [InlineData("unknown-model")]
    public void ResolveCapabilities_UsesConservativeFallbackForUnverifiedOpenAIModel(string modelName)
    {
        var capabilities = ProviderRequestPolicy.ResolveCapabilities(
            "https://api.openai.com/v1",
            modelName);

        Assert.False(capabilities.SupportsThinking);
    }

    [Theory]
    [InlineData(true, "medium", false)]
    [InlineData(false, "none", true)]
    public void Apply_MapsOpenAIThinkingAndSamplingParameters(
        bool enableThinking,
        string expectedEffort,
        bool expectedTemperature)
    {
        var body = new Dictionary<string, object>
        {
            ["temperature"] = 0.3,
            ["top_p"] = 0.8
        };

        ProviderRequestPolicy.Apply(
            body,
            "https://api.openai.com/v1",
            "gpt-5.4",
            enableThinking);

        Assert.Equal(expectedEffort, body["reasoning_effort"]);
        Assert.Equal(expectedTemperature, body.ContainsKey("temperature"));
        Assert.Equal(expectedTemperature, body.ContainsKey("top_p"));
    }

    [Fact]
    public void Apply_DoesNotTrustModelNameOnUnknownEndpoint()
    {
        var body = new Dictionary<string, object> { ["temperature"] = 0.3 };

        ProviderRequestPolicy.Apply(
            body,
            "https://api.openai.com.evil.example/v1",
            "gpt-5.6",
            enableThinking: true);

        Assert.DoesNotContain("reasoning_effort", body.Keys);
        Assert.Contains("temperature", body.Keys);
    }

    private static string? GetThinkingType(Dictionary<string, object> body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body["thinking"]);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("type").GetString();
    }
}
