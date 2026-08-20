namespace QuickTranslate.Services;

/// <summary>
/// Identifies the semantic channel of a streaming provider event. Reasoning is
/// intentionally kept separate from the answer channel so it cannot leak into
/// the rendered, copied, cached, or historical result by accident.
/// </summary>
public enum TranslationStreamEventKind
{
    Started,
    ContentDelta,
    ReasoningDelta,
    Completed
}

public readonly record struct TranslationStreamEvent(
    TranslationStreamEventKind Kind,
    string? Text = null);
