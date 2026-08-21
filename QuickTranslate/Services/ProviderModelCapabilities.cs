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
    string? DisabledReasoningEffort = null,
    bool IsKnownUnsupported = false)
{
    public static ProviderModelCapabilities None { get; } = new(
        ThinkingParameterStyle.None,
        []);

    public bool SupportsThinking => ThinkingStyle != ThinkingParameterStyle.None;
    public bool CanEnableThinking => SupportsThinking;
    public bool CanDisableThinking => ThinkingStyle switch
    {
        ThinkingParameterStyle.ThinkingObject => true,
        ThinkingParameterStyle.EnableThinkingBoolean => true,
        ThinkingParameterStyle.ReasoningEffort => !string.IsNullOrWhiteSpace(DisabledReasoningEffort),
        _ => false
    };

    public ThinkingControlAvailability ThinkingControlAvailability =>
        SupportsThinking
            ? ThinkingControlAvailability.Controllable
            : IsKnownUnsupported
                ? ThinkingControlAvailability.Unsupported
                : ThinkingControlAvailability.Unknown;
}

internal enum ThinkingControlAvailability
{
    Unknown,
    Unsupported,
    Controllable
}
