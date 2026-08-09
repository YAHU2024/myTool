namespace QuickTranslate.Services;

internal static class OpenAIModelCapabilitiesResolver
{
    private static readonly string[] ReasoningModelFamilies =
    [
        "gpt-5.6",
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.2"
    ];

    private static readonly ProviderModelCapabilities ReasoningModel = new(
        ThinkingParameterStyle.ReasoningEffort,
        ["none", "low", "medium", "high", "xhigh"],
        OmitSamplingParametersWhenThinking: true,
        EnabledReasoningEffort: "medium",
        DisabledReasoningEffort: "none");

    private static readonly ProviderModelCapabilities MaxReasoningModel = ReasoningModel with
    {
        SupportedReasoningEfforts = ["none", "low", "medium", "high", "xhigh", "max"]
    };

    public static ProviderModelCapabilities Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return ProviderModelCapabilities.None;

        var model = modelName.Trim();
        if (!ReasoningModelFamilies.Any(family => IsFamily(model, family)))
            return ProviderModelCapabilities.None;

        return IsFamily(model, "gpt-5.6")
            ? MaxReasoningModel
            : ReasoningModel;
    }

    private static bool IsFamily(string modelName, string family) =>
        modelName.Equals(family, StringComparison.OrdinalIgnoreCase) ||
        modelName.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
}
