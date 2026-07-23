using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record BuiltInAnalysisPrompt(string Id, string Name, string PromptTemplate);

/// <summary>
/// The immutable built-in analysis prompt catalog and profile resolution rules.
/// </summary>
public static class AnalysisPromptCatalog
{
    public const string GeneralId = "builtin:general";

    public static IReadOnlyList<BuiltInAnalysisPrompt> BuiltIns { get; } =
    [
        new(GeneralId, "通用解析", "Analyze this text in {targetLang}. Cover its core meaning, key points, grammar, structure, and relevant context. Output only a clear, concise analysis; no preamble or markdown headers."),
        new("builtin:learner", "语言学习", "Analyze this text in {targetLang} as a language tutor. Cover word meaning, grammar, common usage, and pronunciation when relevant. Output only a clear, concise analysis; no preamble or markdown headers."),
        new("builtin:literary", "文学赏析", "Analyze this text in {targetLang} as a literary scholar. Cover rhetorical devices, imagery, symbolism, context, and style when relevant. Output only a clear, concise analysis; no preamble or markdown headers."),
        new("builtin:business", "商务场景", "Analyze this text in {targetLang} for business communication. Cover core meaning, industry terms, implications, and action items when relevant. Output only a clear, concise analysis; no preamble or markdown headers.")
    ];

    public static bool IsBuiltIn(string? id) =>
        BuiltIns.Any(prompt => string.Equals(prompt.Id, id, StringComparison.Ordinal));

    public static BuiltInAnalysisPrompt GetBuiltInOrGeneral(string? id) =>
        BuiltIns.FirstOrDefault(prompt => string.Equals(prompt.Id, id, StringComparison.Ordinal)) ?? BuiltIns[0];

    public static string Resolve(AppSettings settings, string targetLang)
        => Resolve(settings.SelectedAnalysisPromptId, settings.AnalysisPromptProfiles, targetLang);

    public static string Resolve(
        string? selectedId,
        IReadOnlyList<AnalysisPromptProfile> profiles,
        string targetLang)
    {
        profiles ??= Array.Empty<AnalysisPromptProfile>();
        if (selectedId?.StartsWith("custom:", StringComparison.Ordinal) == true)
        {
            var custom = profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, selectedId, StringComparison.Ordinal));
            if (custom != null && !string.IsNullOrWhiteSpace(custom.Prompt))
                return custom.Prompt.Replace("{targetLang}", targetLang, StringComparison.Ordinal);
        }

        var builtIn = GetBuiltInOrGeneral(selectedId);
        return builtIn.PromptTemplate.Replace("{targetLang}", targetLang, StringComparison.Ordinal);
    }
}
