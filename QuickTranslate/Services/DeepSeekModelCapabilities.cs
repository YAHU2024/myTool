namespace QuickTranslate.Services;

internal static class DeepSeekModelCapabilitiesResolver
{
    private static readonly HashSet<string> ThinkingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "deepseek-v4-flash",
        "deepseek-v4-pro"
    };

    private static readonly ProviderModelCapabilities ThinkingModel = new(
        ThinkingParameterStyle.ThinkingObject,
        ["low", "high", "xhigh", "max"],
        OmitSamplingParametersWhenThinking: true,
        ReturnsReasoningContent: true,
        RequiresReasoningContentForToolContinuation: true);

    public static ProviderModelCapabilities Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return ProviderModelCapabilities.None;

        return ThinkingModels.Contains(modelName.Trim())
            ? ThinkingModel
            : ProviderModelCapabilities.None;
    }
}
