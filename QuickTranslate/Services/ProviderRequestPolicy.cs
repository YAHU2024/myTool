namespace QuickTranslate.Services;

internal static class ProviderRequestPolicy
{
    private static readonly string[] SamplingParameters =
    [
        "temperature",
        "top_p",
        "presence_penalty",
        "frequency_penalty"
    ];

    public static ProviderModelCapabilities ResolveCapabilities(string apiBaseUrl, string modelName)
    {
        if (apiBaseUrl.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
            return BigModelModelCapabilitiesResolver.Resolve(modelName);
        if (apiBaseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase))
            return DeepSeekModelCapabilitiesResolver.Resolve(modelName);
        if (apiBaseUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            return SiliconFlowModelCapabilitiesResolver.Resolve(modelName);

        return ProviderModelCapabilities.None;
    }

    public static ProviderModelCapabilities Apply(
        Dictionary<string, object> body,
        string apiBaseUrl,
        string modelName,
        bool enableThinking)
    {
        ArgumentNullException.ThrowIfNull(body);
        var capabilities = ResolveCapabilities(apiBaseUrl, modelName);
        if (!capabilities.SupportsThinking)
            return capabilities;

        switch (capabilities.ThinkingStyle)
        {
            case ThinkingParameterStyle.ThinkingObject:
                body["thinking"] = new { type = enableThinking ? "enabled" : "disabled" };
                break;
            case ThinkingParameterStyle.EnableThinkingBoolean:
                body["enable_thinking"] = enableThinking;
                break;
        }

        if (enableThinking && capabilities.OmitSamplingParametersWhenThinking)
        {
            foreach (var parameter in SamplingParameters)
                body.Remove(parameter);
        }

        return capabilities;
    }
}
