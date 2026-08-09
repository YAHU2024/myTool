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
        return ProviderEndpointResolver.Resolve(apiBaseUrl) switch
        {
            ProviderKind.BigModel => BigModelModelCapabilitiesResolver.Resolve(modelName),
            ProviderKind.DeepSeek => DeepSeekModelCapabilitiesResolver.Resolve(modelName),
            ProviderKind.SiliconFlow => SiliconFlowModelCapabilitiesResolver.Resolve(modelName),
            ProviderKind.OpenAI => OpenAIModelCapabilitiesResolver.Resolve(modelName),
            _ => ProviderModelCapabilities.None
        };
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
            case ThinkingParameterStyle.ReasoningEffort:
                var effort = enableThinking
                    ? capabilities.EnabledReasoningEffort
                    : capabilities.DisabledReasoningEffort;
                if (!string.IsNullOrWhiteSpace(effort))
                    body["reasoning_effort"] = effort;
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
