using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class AnalysisConversationFormatterTests
{
    [Fact]
    public void NormalizeQuestion_TrimsAndCountsUnicodeScalars()
    {
        Assert.Equal("问题", AnalysisConversationFormatter.NormalizeQuestion("  问题  "));
        var valid = string.Concat(Enumerable.Repeat("😀", AnalysisConversationFormatter.MaxQuestionRunes));
        Assert.Equal(valid, AnalysisConversationFormatter.NormalizeQuestion(valid));
        Assert.Throws<ArgumentException>(() => AnalysisConversationFormatter.NormalizeQuestion(valid + "x"));
    }

    [Fact]
    public void SummarizeQuestion_NormalizesWhitespaceAndDoesNotSplitSurrogates()
    {
        var question = string.Concat(Enumerable.Repeat("😀", AnalysisConversationFormatter.SummaryRunes + 1));

        var summary = AnalysisConversationFormatter.SummarizeQuestion("  " + question + "\r\n  ");

        Assert.Equal(string.Concat(Enumerable.Repeat("😀", AnalysisConversationFormatter.SummaryRunes)) + "...", summary);
    }

    [Fact]
    public void BuildCopyText_IncludesOnlyCompletedTurns()
    {
        AnalysisFollowUpTurnState[] turns =
        [
            new(1, "q1", "a1", AnalysisFollowUpTurnStatus.Completed, 1),
            new(2, "q2", "partial", AnalysisFollowUpTurnStatus.Failed, 2),
            new(3, "q3", "a3", AnalysisFollowUpTurnStatus.Completed, 3)
        ];

        var text = AnalysisConversationFormatter.BuildCopyText("root", turns);

        Assert.Equal("## 解析\r\nroot\r\n\r\n## Q1\r\nq1\r\n\r\na1\r\n\r\n## Q3\r\nq3\r\n\r\na3", NormalizeNewlines(text));
        Assert.DoesNotContain("q2", text);
        Assert.DoesNotContain("partial", text);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
