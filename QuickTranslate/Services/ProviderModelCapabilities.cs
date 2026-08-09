namespace QuickTranslate.Services;

internal enum ThinkingParameterStyle
{
    None,
    ThinkingObject,
    EnableThinkingBoolean,
    ReasoningEffort
}

internal sealed record ProviderModelCapabilities(
    ThinkingParameterStyle ThinkingStyle,
    IReadOnlyList<string> SupportedReasoningEfforts,
    bool OmitSamplingParametersWhenThinking = false,
    bool ReturnsReasoningContent = false,
    bool RequiresReasoningContentForToolContinuation = false,
    string? EnabledReasoningEffort = null,
    string? DisabledReasoningEffort = null)
{
    public static ProviderModelCapabilities None { get; } = new(
        ThinkingParameterStyle.None,
        []);

    public bool SupportsThinking => ThinkingStyle != ThinkingParameterStyle.None;
}
