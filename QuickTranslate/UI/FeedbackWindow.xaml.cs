using System.Windows;
using System.Windows.Controls;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

public partial class FeedbackWindow : Window
{
    private readonly FeedbackContentBuilder _builder = new();
    private readonly FeedbackMode _mode;
    private readonly FeedbackDiagnosticSummary? _diagnostics;
    private readonly Action? _feedbackStarted;
    private bool _diagnosticsCleared;
    private IReadOnlyList<FeedbackField> _fields = Array.Empty<FeedbackField>();

    public FeedbackWindow(
        FeedbackMode mode = FeedbackMode.Problem,
        FeedbackDiagnosticSummary? diagnostics = null,
        Action? feedbackStarted = null)
    {
        _mode = mode;
        _diagnostics = diagnostics;
        _feedbackStarted = feedbackStarted;
        InitializeComponent();
        CategoryComboBox.SelectionChanged += (_, _) => RefreshPreview();
        DescriptionTextBox.TextChanged += (_, _) => RefreshPreview();
        ReproductionTextBox.TextChanged += (_, _) => RefreshPreview();
        ExpectedTextBox.TextChanged += (_, _) => RefreshPreview();
        CategoryComboBox.ItemsSource = mode == FeedbackMode.FeatureRequest
            ? new[] { "翻译体验", "查词体验", "快捷键/取词", "界面显示", "更新", "其他" }
            : new[] { "翻译", "查词", "快捷键/取词", "界面显示", "更新", "其他" };
        CategoryComboBox.SelectedIndex = 0;
        TitleTextBlock.Text = mode == FeedbackMode.FeatureRequest ? "提出功能建议" :
            mode == FeedbackMode.CrashRecovery ? "反馈上次异常退出" : "报告问题";
        if (mode == FeedbackMode.CrashRecovery)
        {
            DescriptionTextBox.Text = "QuickTranslate 上次运行未正常结束。";
            DescriptionTextBox.CaretIndex = DescriptionTextBox.Text.Length;
        }
        else if (mode == FeedbackMode.FeatureRequest)
        {
            CategoryLabel.Text = "建议类别";
            DescriptionLabel.Text = "使用场景";
            ReproductionLabel.Text = "可接受的替代方案（可选）";
            ExpectedLabel.Text = "建议的行为或界面";
        }
        RefreshPreview();
    }

    private FeedbackDraft BuildDraft() => new(
        _mode == FeedbackMode.FeatureRequest ? FeedbackMode.FeatureRequest : FeedbackMode.Problem,
        CategoryComboBox.SelectedItem?.ToString() ?? "其他",
        DescriptionTextBox.Text,
        ReproductionTextBox.Text,
        ExpectedTextBox.Text,
        _diagnostics is null || _diagnosticsCleared
            ? null
            : _diagnostics with { Category = CategoryComboBox.SelectedItem?.ToString() ?? "其他" });

    private void RefreshPreview()
    {
        var draft = BuildDraft();
        _fields = _builder.BuildFields(draft);
        FieldsPanel.Children.Clear();
        foreach (var field in _fields)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var button = new Button { Content = "复制", Tag = field, Padding = new Thickness(8, 3, 8, 3) };
            button.Click += CopyFieldButton_Click;
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            FieldsPanel.Children.Add(row);
        }
        PreviewTextBox.Text = _builder.BuildCopyAllMarkdown(draft);
    }

    private bool ConfirmSensitiveContent(FeedbackDraft draft)
    {
        var values = new[] { draft.Description, draft.Reproduction, draft.Expected };
        if (!values.Any(_builder.ContainsSensitivePattern))
            return true;

        return MessageBox.Show(
            "输入内容可能包含密钥、令牌、路径或其他敏感信息。公开 GitHub Issue 任何人都可能看到。仍要继续吗？",
            "请确认公开风险",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void CopyFieldButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft();
        if (!ConfirmSensitiveContent(draft) || sender is not Button { Tag: FeedbackField field })
            return;
        try
        {
            Clipboard.SetText(field.Value);
            StatusTextBlock.Text = $"已复制“{field.Label}”，请粘贴到 GitHub 表单对应字段。";
        }
        catch
        {
            StatusTextBlock.Text = "复制失败，请手动选择预览内容复制。";
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft();
        if (!ConfirmSensitiveContent(draft))
            return;
        try
        {
            Clipboard.SetText(_builder.BuildCopyAllMarkdown(draft));
            StatusTextBlock.Text = "已复制全部字段，请在 GitHub 页面逐项粘贴并检查。";
        }
        catch
        {
            StatusTextBlock.Text = "复制失败，请手动选择预览内容复制。";
        }
    }

    private void ClearDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsCleared = true;
        RefreshPreview();
        StatusTextBlock.Text = "已清除诊断摘要。";
    }

    private void OpenGithubButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft();
        if (string.IsNullOrWhiteSpace(draft.Description))
        {
            MessageBox.Show("请先填写反馈内容。", "无法继续", MessageBoxButton.OK, MessageBoxImage.Information);
            DescriptionTextBox.Focus();
            return;
        }
        if (!ConfirmSensitiveContent(draft))
            return;

        _feedbackStarted?.Invoke();
        if (FeedbackLinkService.TryOpen(_mode))
            StatusTextBlock.Text = "GitHub 表单已打开。请逐项粘贴、检查内容后再提交。";
        else
            StatusTextBlock.Text = "无法打开浏览器，请使用上方“复制全部字段”后手动打开 GitHub 反馈页。";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
