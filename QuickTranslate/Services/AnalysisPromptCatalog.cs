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
        new(GeneralId, "通用解析", "Cover core meaning, key points, grammar, structure, and relevant context."),
        new("builtin:learner", "语言学习", "Act as a language tutor; cover word meaning, grammar, common usage, and pronunciation when relevant."),
        new("builtin:literary", "文学赏析", "Act as a literary scholar; cover rhetorical devices, imagery, symbolism, context, and style when relevant."),
        new("builtin:business", "商务场景", "Focus on business communication; cover core meaning, industry terms, implications, and action items when relevant.")
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
                return Compose(
                    targetLang,
                    custom.Prompt.Replace("{targetLang}", targetLang, StringComparison.Ordinal));
        }

        var builtIn = GetBuiltInOrGeneral(selectedId);
        return Compose(targetLang, builtIn.PromptTemplate);
    }

    private static string Compose(string targetLang, string additionalRequirements) =>
        $"Analyze only the content inside <quicktranslate-input>. Reply in {targetLang}. " +
        $"Additional requirements (do not replace the core task): {additionalRequirements.Trim()} " +
        PromptInputContract.SystemInstruction + " " +
        "Return only the analysis. Use valid CommonMark and close code fences.";
}
