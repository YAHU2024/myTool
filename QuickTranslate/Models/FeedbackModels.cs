namespace QuickTranslate.Models;

public enum FeedbackMode
{
    Problem,
    FeatureRequest,
    CrashRecovery
}

public sealed record FeedbackDraft(
    FeedbackMode Mode,
    string Category,
    string Description,
    string Reproduction,
    string Expected,
    FeedbackDiagnosticSummary? Diagnostics = null);

public sealed record FeedbackDiagnosticSummary(
    string AppVersion,
    string OsVersion,
    string Architecture,
    string Category,
    string OccurredAt,
    string ErrorType = "",
    string ErrorCode = "");

public sealed record FeedbackField(string Id, string Label, string Value);
