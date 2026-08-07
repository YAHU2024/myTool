using System.Text;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

internal static class AnalysisConversationFormatter
{
    public const int MaxQuestionRunes = 2000;
    public const int SummaryRunes = 20;

    public static string NormalizeQuestion(string? question)
    {
        var normalized = question?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("追问不能为空", nameof(question));
        if (normalized.EnumerateRunes().Count() > MaxQuestionRunes)
            throw new ArgumentException($"追问不能超过 {MaxQuestionRunes} 个字符", nameof(question));
        return normalized;
    }

    public static string SummarizeQuestion(string question)
    {
        var normalized = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var runes = normalized.EnumerateRunes().Take(SummaryRunes).ToArray();
        var summary = string.Concat(runes.Select(rune => rune.ToString()));
        return normalized.EnumerateRunes().Count() > SummaryRunes ? summary + "..." : summary;
    }

    public static string BuildCopyText(string rootAnalysis, IReadOnlyList<AnalysisFollowUpTurnState> turns)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 解析");
        builder.Append(rootAnalysis.Trim());
        foreach (var turn in turns.Where(turn => turn.Status == AnalysisFollowUpTurnStatus.Completed))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append("## Q").AppendLine(turn.TurnNumber.ToString());
            builder.AppendLine(turn.Question);
            builder.AppendLine();
            builder.Append(turn.AnswerRawText.Trim());
        }
        return builder.ToString();
    }
}
