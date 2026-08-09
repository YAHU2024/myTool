namespace QuickTranslate.Services;

internal sealed record BigModelModelCapabilities(
    bool SupportsThinking,
    bool SupportsReasoningEffort);

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

    private static readonly BigModelModelCapabilities NoThinkingModel = new(false, false);

    public static BigModelModelCapabilities Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return NoThinkingModel;

        var model = modelName.Trim();
        if (!ThinkingModelFamilies.Any(family => IsFamily(model, family)))
            return NoThinkingModel;

        return new BigModelModelCapabilities(
            SupportsThinking: true,
            SupportsReasoningEffort: IsFamily(model, "glm-5.2"));
    }

    private static bool IsFamily(string modelName, string family) =>
        modelName.Equals(family, StringComparison.OrdinalIgnoreCase) ||
        modelName.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
}
