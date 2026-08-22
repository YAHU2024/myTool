using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class FeedbackLinkServiceTests
{
    [Fact]
    public void GetIssueFormUrl_UsesFixedBugTemplate()
    {
        Assert.Equal(
            "https://github.com/YAHU2024/myTool/issues/new?template=bug_report.yml",
            FeedbackLinkService.GetIssueFormUrl(FeedbackMode.Problem));
        Assert.DoesNotContain("description", FeedbackLinkService.GetIssueFormUrl(FeedbackMode.Problem));
    }

    [Fact]
    public void GetIssueFormUrl_UsesFeatureTemplateOnlyForFeatureMode()
    {
        Assert.Equal(
            "https://github.com/YAHU2024/myTool/issues/new?template=feature_request.yml",
            FeedbackLinkService.GetIssueFormUrl(FeedbackMode.FeatureRequest));
        Assert.Equal(
            FeedbackLinkService.BugReportUrl,
            FeedbackLinkService.GetIssueFormUrl(FeedbackMode.CrashRecovery));
    }
}
