using QuickTranslate.Models;

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

    public static bool? ResolveThinkingRequestValue(
        string apiBaseUrl,
        string modelName,
        ThinkingModePreference preference) =>
        ResolveThinkingRequestValue(
            ThinkingModePreferences.Normalize(preference),
            ResolveCapabilities(apiBaseUrl, modelName));

    private static bool? ResolveThinkingRequestValue(
        ThinkingModePreference preference,
        ProviderModelCapabilities capabilities) => preference switch
        {
            ThinkingModePreference.Enabled when capabilities.CanEnableThinking => true,
            ThinkingModePreference.Disabled when capabilities.CanDisableThinking => false,
            _ => null
        };

    public static ProviderModelCapabilities Apply(
        Dictionary<string, object> body,
        string apiBaseUrl,
        string modelName,
        bool? enableThinking)
    {
        ArgumentNullException.ThrowIfNull(body);
        var capabilities = ResolveCapabilities(apiBaseUrl, modelName);
        if (!capabilities.SupportsThinking || enableThinking is null)
            return capabilities;

        var shouldEnable = enableThinking.Value;

        switch (capabilities.ThinkingStyle)
        {
            case ThinkingParameterStyle.ThinkingObject:
                body["thinking"] = new { type = shouldEnable ? "enabled" : "disabled" };
                break;
            case ThinkingParameterStyle.EnableThinkingBoolean:
                body["enable_thinking"] = shouldEnable;
                break;
            case ThinkingParameterStyle.ReasoningEffort:
                var effort = shouldEnable
                    ? capabilities.EnabledReasoningEffort
                    : capabilities.DisabledReasoningEffort;
                if (!string.IsNullOrWhiteSpace(effort))
                    body["reasoning_effort"] = effort;
                break;
        }

        if (shouldEnable && capabilities.OmitSamplingParametersWhenThinking)
        {
            foreach (var parameter in SamplingParameters)
                body.Remove(parameter);
        }

        return capabilities;
    }
}
