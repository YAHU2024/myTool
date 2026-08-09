namespace QuickTranslate.Services;

internal static class BigModelModelCapabilitiesResolver
{
    private static readonly string[] ThinkingModelFamilies =
    [
        "glm-5.2",
        "glm-5.1",
        "glm-5v-turbo",
        "glm-5-turbo",
        "glm-5",
        "glm-4.7",
        "glm-4.6v",
        "glm-4.6",
        "glm-4.5v",
        "glm-4.5"
    ];

    private static readonly ProviderModelCapabilities ThinkingModel = new(
        ThinkingParameterStyle.ThinkingObject,
        []);

    private static readonly ProviderModelCapabilities ReasoningEffortModel = new(
        ThinkingParameterStyle.ThinkingObject,
        ["max", "xhigh", "high", "medium", "low", "minimal", "none"]);

    public static ProviderModelCapabilities Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return ProviderModelCapabilities.None;

        var model = modelName.Trim();
        if (!ThinkingModelFamilies.Any(family => IsFamily(model, family)))
            return ProviderModelCapabilities.None;

        return IsFamily(model, "glm-5.2")
            ? ReasoningEffortModel
            : ThinkingModel;
    }

    private static bool IsFamily(string modelName, string family) =>
        modelName.Equals(family, StringComparison.OrdinalIgnoreCase) ||
        modelName.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
}
