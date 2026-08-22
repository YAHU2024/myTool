using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class FeedbackContentBuilderTests
{
    private readonly FeedbackContentBuilder _builder = new();

    [Fact]
    public void BuildFields_UsesOnlyExpectedUserAndDiagnosticFields()
    {
        var fields = _builder.BuildFields(new FeedbackDraft(
            FeedbackMode.Problem,
            "翻译",
            "窗口没有显示结果",
            "1. 选中文本\n2. 点击快捷键",
            "应显示结果",
            new FeedbackDiagnosticSummary("1.2.3", "Windows 11", "X64", "翻译", "2026-08-22 12:00")));

        Assert.Equal(new[] { "category", "what_happened", "reproduction", "expected", "environment" }, fields.Select(x => x.Id));
        Assert.DoesNotContain(fields, field => field.Value.Contains("api", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FeatureRequest_IncludesCategoryFieldForIssueForm()
    {
        var fields = _builder.BuildFields(new FeedbackDraft(
            FeedbackMode.FeatureRequest, "界面显示", "希望支持紧凑模式", "", "提供一个开关"));

        Assert.Equal("category", fields[0].Id);
        Assert.Equal("建议类别", fields[0].Label);
    }

    [Fact]
    public void BuildFields_TruncatesUserInput()
    {
        var fields = _builder.BuildFields(new FeedbackDraft(
            FeedbackMode.Problem, "其他", new string('x', FeedbackContentBuilder.MaxUserFieldLength + 50), "", ""));

        var description = Assert.Single(fields, field => field.Id == "what_happened");
        Assert.Equal(FeedbackContentBuilder.MaxUserFieldLength, description.Value.Length);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc")]
    [InlineData("api_key=secret")]
    [InlineData("C:\\Users\\Alice\\AppData\\Roaming")]
    public void ContainsSensitivePattern_Warns(string value)
    {
        Assert.True(_builder.ContainsSensitivePattern(value));
    }

    [Fact]
    public void BuildCopyAllMarkdown_UsesFieldHeadersForIssueFormPasting()
    {
        var markdown = _builder.BuildCopyAllMarkdown(new FeedbackDraft(
            FeedbackMode.FeatureRequest, "界面显示", "希望支持紧凑模式", "手动调整窗口", "提供一个开关"));

        Assert.Contains("## 使用场景", markdown, StringComparison.Ordinal);
        Assert.Contains("## 建议的行为或界面", markdown, StringComparison.Ordinal);
        Assert.Contains("## 可接受的替代方案", markdown, StringComparison.Ordinal);
    }
}
