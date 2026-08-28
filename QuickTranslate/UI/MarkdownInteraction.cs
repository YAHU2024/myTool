using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

/// <summary>
/// Shared actions for every selectable Markdown host. Selection lifecycle remains
/// owned by the containing view because streaming updates have different scopes.
/// </summary>
internal static class MarkdownInteraction
{
    /// <param name="scrollable">
    /// When true the host keeps an auto vertical scrollbar (e.g. a full changelog
    /// document); when false the host disables its own scrollbar because the
    /// parent view manages scrolling (streaming conversation).
    /// </param>
    public static void ConfigureSelectableHost(
        RichTextBox markdown,
        string automationName,
        bool scrollable = false)
    {
        markdown.IsReadOnly = true;
        markdown.IsUndoEnabled = false;
        markdown.IsReadOnlyCaretVisible = false;
        markdown.IsDocumentEnabled = true;
        markdown.Focusable = true;
        markdown.IsTabStop = false;
        markdown.BorderThickness = new Thickness(0);
        markdown.Background = System.Windows.Media.Brushes.Transparent;
        markdown.Padding = new Thickness(0);
        markdown.VerticalScrollBarVisibility = scrollable
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        markdown.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        markdown.SelectionBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4D, 0xB6, 0xAC));
        markdown.SelectionOpacity = 0.45;
        markdown.Cursor = System.Windows.Input.Cursors.IBeam;
        AutomationProperties.SetName(markdown, automationName);
        AttachActions(markdown);
    }

    public static void AttachActions(RichTextBox markdown)
    {
        markdown.AddHandler(Button.ClickEvent, new RoutedEventHandler(CodeCopyButton_Click));
        markdown.AddHandler(
            Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(Link_RequestNavigate));
    }

    private static void CodeCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: MarkdownCodeBlock metadata } button)
            return;

        try
        {
            Clipboard.SetText(metadata.Code);
            TransientButtonFeedback.ShowCopySuccess(button, "\u29C9");
            e.Handled = true;
        }
        catch
        {
            // Clipboard access can be temporarily unavailable.
        }
    }

    private static void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!MarkdownRenderer.IsSafeLink(e.Uri?.AbsoluteUri, out var uri) || uri is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Logger.Warn("MarkdownInteraction", $"Could not open a Markdown link: {exception.GetType().Name}");
        }
    }
}
