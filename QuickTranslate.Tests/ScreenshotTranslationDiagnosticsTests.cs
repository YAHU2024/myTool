using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationDiagnosticsTests
{
    [Fact]
    public void CancellationReason_IsFirstCauseWins()
    {
        var diagnostics = new ScreenshotTranslationDiagnostics("session");

        Assert.True(diagnostics.TryRecordCancellation(ScreenshotTranslationCancellationReason.User));
        Assert.False(diagnostics.TryRecordCancellation(ScreenshotTranslationCancellationReason.OverlayClosed));
        Assert.Equal(ScreenshotTranslationCancellationReason.User, diagnostics.CancellationReason);
        Assert.Equal("session", diagnostics.SessionId);
    }

    [Fact]
    public void Evaluate_RecordsPrivacySafeActionAndReasonCounts()
    {
        var diagnostics = new ScreenshotTranslationDiagnostics();

        diagnostics.Evaluate("https://example.com", "简体中文");
        diagnostics.Evaluate("This sentence needs translation.", "简体中文");
        diagnostics.Evaluate("中文", "简体中文");
        diagnostics.Evaluate("---", "简体中文");

        var snapshot = diagnostics.Snapshot();

        Assert.Equal(4, snapshot.EvaluatedCount);
        Assert.Equal(1, snapshot.TranslateCount);
        Assert.Equal(2, snapshot.PreserveCount);
        Assert.Equal(1, snapshot.SkipCount);
        Assert.Equal(1, snapshot.ReasonCounts[nameof(ScreenshotTranslationEligibilityReason.Url)]);
        Assert.Equal(1, snapshot.ReasonCounts[nameof(ScreenshotTranslationEligibilityReason.NaturalLanguage)]);
        Assert.Equal(1, snapshot.ReasonCounts[nameof(ScreenshotTranslationEligibilityReason.TargetLanguageMatch)]);
        Assert.Equal(1, snapshot.ReasonCounts[nameof(ScreenshotTranslationEligibilityReason.NonText)]);
    }
}
