namespace QuickTranslate.Models;

/// <summary>
/// A user-owned deep-analysis prompt profile.
/// </summary>
public sealed class AnalysisPromptProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;

    public AnalysisPromptProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Prompt = Prompt
    };
}
