using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace QuickTranslate.Helpers;

internal sealed record MarkdownCodeBlock(string Code, string? Language);

/// <summary>
/// A rendered document that always retains the complete source text. On failure the document
/// contains that source as plain text, so callers never need to reconstruct or truncate it.
/// </summary>
internal sealed record MarkdownRenderResult(
    FlowDocument Document,
    string RawText,
    string DisplayedRawText,
    IReadOnlyList<MarkdownCodeBlock> CodeBlocks,
    bool IsCollapsed,
    bool UsedPlainTextFallback,
    Exception? Error);

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)));
    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0xA9, 0xFF)));
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)));
    private static readonly Brush QuoteBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)));
    private static readonly Brush BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));

    public const int DefaultMaxDisplayCharacters = 20_000;

    public static FlowDocument Render(string rawText) => RenderDetailed(rawText).Document;

    public static MarkdownRenderResult RenderDetailed(
        string rawText,
        int maxDisplayCharacters = DefaultMaxDisplayCharacters)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        if (maxDisplayCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDisplayCharacters));

        var markdown = Markdown.Parse(rawText, Pipeline);
        var displayedRawText = GetDisplayText(rawText, markdown, maxDisplayCharacters);
        var document = CreateDocument();
        var codeBlocks = new List<MarkdownCodeBlock>();
        var displayedMarkdown = displayedRawText.Length == rawText.Length
            ? markdown
            : Markdown.Parse(displayedRawText, Pipeline);
        AppendBlocks(displayedMarkdown, document.Blocks, codeBlocks);
        return new MarkdownRenderResult(
            document,
            rawText,
            displayedRawText,
            codeBlocks.AsReadOnly(),
            displayedRawText.Length != rawText.Length,
            false,
            null);
    }

    public static bool TryRender(
        string rawText,
        out MarkdownRenderResult result,
        int maxDisplayCharacters = DefaultMaxDisplayCharacters)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        try
        {
            result = RenderDetailed(rawText, maxDisplayCharacters);
            return true;
        }
        catch (Exception exception)
        {
            var fallback = CreateDocument();
            fallback.Blocks.Add(new Paragraph(new Run(rawText)));
            result = new MarkdownRenderResult(
                fallback,
                rawText,
                rawText,
                Array.Empty<MarkdownCodeBlock>(),
                false,
                true,
                exception);
            return false;
        }
    }

    internal static bool IsSafeLink(string? url, out Uri? uri)
    {
        var valid = Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        if (!valid)
            uri = null;
        return valid;
    }

    private static string GetDisplayText(string rawText, MarkdownDocument markdown, int maxDisplayCharacters)
    {
        if (rawText.Length <= maxDisplayCharacters)
            return rawText;

        var finalEnd = -1;
        foreach (var block in markdown)
        {
            if (block.Span.End >= maxDisplayCharacters)
                break;
            finalEnd = block.Span.End;
        }

        // A single oversized first block cannot be folded without breaking Markdown syntax.
        return finalEnd < 0 ? rawText : rawText[..(finalEnd + 1)];
    }

    private static FlowDocument CreateDocument() => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
        Foreground = TextBrush,
        PagePadding = new Thickness(0),
        TextAlignment = TextAlignment.Left
    };

    private static void AppendBlocks(
        ContainerBlock source,
        BlockCollection target,
        List<MarkdownCodeBlock> codeBlocks)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    target.Add(CreateHeading(heading));
                    break;
                case ParagraphBlock paragraph:
                    target.Add(CreateParagraph(paragraph));
                    break;
                case FencedCodeBlock fenced:
                    target.Add(CreateCodeBlock(fenced, fenced.Info?.ToString(), codeBlocks));
                    break;
                case CodeBlock code:
                    target.Add(CreateCodeBlock(code, null, codeBlocks));
                    break;
                case ListBlock list:
                    target.Add(CreateList(list, codeBlocks));
                    break;
                case QuoteBlock quote:
                    target.Add(CreateQuote(quote, codeBlocks));
                    break;
                case ThematicBreakBlock:
                    target.Add(CreateThematicBreak());
                    break;
                case Markdig.Extensions.Tables.Table table:
                    target.Add(CreateTable(table, codeBlocks));
                    break;
                case HtmlBlock:
                    break;
                case ContainerBlock container:
                    AppendBlocks(container, target, codeBlocks);
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    target.Add(CreateParagraph(leaf));
                    break;
            }
        }
    }

    private static Paragraph CreateHeading(HeadingBlock heading)
    {
        var paragraph = CreateParagraph(heading);
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = heading.Level switch
        {
            1 => 24,
            2 => 21,
            3 => 18,
            _ => 16
        };
        paragraph.Margin = new Thickness(0, heading.Level <= 2 ? 12 : 8, 0, 5);
        return paragraph;
    }

    private static Paragraph CreateParagraph(LeafBlock block)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 7) };
        if (block.Inline is not null)
            AppendInlines(block.Inline.FirstChild, paragraph.Inlines);
        return paragraph;
    }

    private static BlockUIContainer CreateCodeBlock(
        CodeBlock block,
        string? language,
        List<MarkdownCodeBlock> codeBlocks)
    {
        var code = block.Lines.ToString();
        var metadata = new MarkdownCodeBlock(
            code,
            string.IsNullOrWhiteSpace(language) ? null : language.Trim());
        codeBlocks.Add(metadata);

        var copyButton = new Button
        {
            Content = "\u29C9",
            Tag = metadata,
            ToolTip = "复制代码",
            Margin = new Thickness(0, 4, 4, 0),
            Focusable = false,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        copyButton.SetResourceReference(FrameworkElement.StyleProperty, "IconToolbarButton");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = metadata.Language ?? "代码",
            Foreground = MutedBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(10, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        });
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);
        DockPanel.SetDock(header, Dock.Top);
        var text = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(10)
        };
        var content = new DockPanel();
        content.Children.Add(header);
        content.Children.Add(text);
        var border = new Border
        {
            Background = CodeBackground,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content,
            Tag = metadata
        };
        return new BlockUIContainer(border) { Margin = new Thickness(0, 4, 0, 9) };
    }

    private static System.Windows.Documents.List CreateList(
        ListBlock source,
        List<MarkdownCodeBlock> codeBlocks)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 2, 0, 7),
            Padding = new Thickness(6, 0, 0, 0)
        };
        foreach (var child in source)
        {
            if (child is not ListItemBlock sourceItem)
                continue;
            var item = new ListItem { Margin = new Thickness(0, 1, 0, 2) };
            AppendBlocks(sourceItem, item.Blocks, codeBlocks);
            list.ListItems.Add(item);
        }
        return list;
    }

    private static Section CreateQuote(QuoteBlock quote, List<MarkdownCodeBlock> codeBlocks)
    {
        var section = new Section
        {
            Background = QuoteBackground,
            Foreground = MutedBrush,
            Padding = new Thickness(12, 7, 10, 3),
            Margin = new Thickness(0, 4, 0, 8)
        };
        AppendBlocks(quote, section.Blocks, codeBlocks);
        return section;
    }

    private static BlockUIContainer CreateThematicBreak() => new(new Border
    {
        Height = 1,
        Background = BorderBrush,
        Margin = new Thickness(0, 8, 0, 8)
    });

    private static System.Windows.Documents.Table CreateTable(
        Markdig.Extensions.Tables.Table source,
        List<MarkdownCodeBlock> codeBlocks)
    {
        var table = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 9)
        };
        var columnCount = source.OfType<Markdig.Extensions.Tables.TableRow>()
            .Select(row => row.Count)
            .DefaultIfEmpty(0)
            .Max();
        for (var index = 0; index < columnCount; index++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var sourceRow in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var row = new System.Windows.Documents.TableRow
            {
                FontWeight = sourceRow.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                Background = sourceRow.IsHeader ? QuoteBackground : Brushes.Transparent
            };
            group.Rows.Add(row);
            foreach (var sourceCell in sourceRow.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var cell = new System.Windows.Documents.TableCell
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(7, 4, 7, 4)
                };
                AppendBlocks(sourceCell, cell.Blocks, codeBlocks);
                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new Paragraph());
                row.Cells.Add(cell);
            }
        }
        return table;
    }

    private static void AppendInlines(Markdig.Syntax.Inlines.Inline? source, InlineCollection target)
    {
        for (var current = source; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = CodeBackground,
                        Foreground = TextBrush
                    });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span
                    {
                        FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = emphasis.DelimiterCount % 2 == 1 ? FontStyles.Italic : FontStyles.Normal
                    };
                    AppendInlines(emphasis.FirstChild, span.Inlines);
                    target.Add(span);
                    break;
                case LinkInline link when link.IsImage:
                    break;
                case LinkInline link:
                    AppendLink(link, target);
                    break;
                case AutolinkInline autoLink:
                    AppendAutoLink(autoLink, target);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case HtmlInline:
                    break;
                case ContainerInline container:
                    AppendInlines(container.FirstChild, target);
                    break;
            }
        }
    }

    private static void AppendLink(LinkInline link, InlineCollection target)
    {
        var url = link.GetDynamicUrl is null ? link.Url : link.GetDynamicUrl();
        if (!IsSafeLink(url, out var uri))
        {
            AppendInlines(link.FirstChild, target);
            return;
        }

        var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = LinkBrush };
        AppendInlines(link.FirstChild, hyperlink.Inlines);
        target.Add(hyperlink);
    }

    private static void AppendAutoLink(AutolinkInline link, InlineCollection target)
    {
        if (!IsSafeLink(link.Url, out var uri))
        {
            target.Add(new Run(link.Url));
            return;
        }

        target.Add(new Hyperlink(new Run(link.Url)) { NavigateUri = uri, Foreground = LinkBrush });
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
