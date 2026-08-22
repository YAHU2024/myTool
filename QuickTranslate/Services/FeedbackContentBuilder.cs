using System.Text;
using System.Text.RegularExpressions;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class FeedbackContentBuilder
{
    private static readonly Regex SensitivePattern = new(
        "(?i)(api[_-]?key|authorization|bearer\\s+|password|passwd|secret|token|cookie|private[_-]?key|[A-Z]:\\\\Users\\\\|AppData|\\b(sk|pk)-[A-Za-z0-9_-]{8,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxUserFieldLength = 4000;

    public IReadOnlyList<FeedbackField> BuildFields(FeedbackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var fields = new List<FeedbackField>();
        if (draft.Mode == FeedbackMode.FeatureRequest)
        {
            fields.Add(new("category", "建议类别", Limit(draft.Category)));
            fields.Add(new("use_case", "使用场景", Limit(draft.Description)));
            fields.Add(new("proposal", "建议的行为或界面", Limit(draft.Expected)));
            fields.Add(new("alternatives", "可接受的替代方案", Limit(draft.Reproduction)));
        }
        else
        {
            fields.Add(new("category", "问题类别", Limit(draft.Category)));
            fields.Add(new("what_happened", "发生了什么", Limit(draft.Description)));
            fields.Add(new("reproduction", "如何复现", Limit(draft.Reproduction)));
            fields.Add(new("expected", "期望结果", Limit(draft.Expected)));
        }

        if (draft.Diagnostics is not null)
        {
            fields.Add(new("environment", "应用与系统信息", BuildDiagnosticSummary(draft.Diagnostics)));
        }

        return fields;
    }

    public string BuildCopyAllMarkdown(FeedbackDraft draft)
    {
        var builder = new StringBuilder();
        foreach (var field in BuildFields(draft))
        {
            builder.Append("## ").AppendLine(field.Label);
            builder.AppendLine(field.Value);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public bool ContainsSensitivePattern(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SensitivePattern.IsMatch(value);

    public string BuildDiagnosticSummary(FeedbackDiagnosticSummary summary)
    {
        var lines = new List<string>
        {
            $"应用版本：{Safe(summary.AppVersion)}",
            $"Windows：{Safe(summary.OsVersion)}",
            $"架构：{Safe(summary.Architecture)}",
            $"问题类别：{Safe(summary.Category)}",
            $"发生时间：{Safe(summary.OccurredAt)}"
        };
        if (!string.IsNullOrWhiteSpace(summary.ErrorType))
            lines.Add($"异常类型：{Safe(summary.ErrorType)}");
        if (!string.IsNullOrWhiteSpace(summary.ErrorCode))
            lines.Add($"错误代码：{Safe(summary.ErrorCode)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Limit(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= MaxUserFieldLength
            ? normalized
            : normalized[..MaxUserFieldLength];
    }

    private static string Safe(string? value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
}
