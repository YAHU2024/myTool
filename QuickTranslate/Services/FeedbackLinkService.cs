using System.Diagnostics;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public static class FeedbackLinkService
{
    public const string RepositoryUrl = "https://github.com/YAHU2024/myTool";
    public const string BugReportUrl = RepositoryUrl + "/issues/new?template=bug_report.yml";
    public const string FeatureRequestUrl = RepositoryUrl + "/issues/new?template=feature_request.yml";

    public static string GetIssueFormUrl(FeedbackMode mode) =>
        mode == FeedbackMode.FeatureRequest ? FeatureRequestUrl : BugReportUrl;

    public static bool TryOpen(FeedbackMode mode)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GetIssueFormUrl(mode),
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
