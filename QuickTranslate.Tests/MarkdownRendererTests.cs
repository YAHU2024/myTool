using System.Windows.Controls;
using System.Windows.Documents;
using QuickTranslate.Helpers;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MarkdownRendererTests
{
    [Fact]
    public void RenderDetailed_RendersCoreBlocksAndInlineFormatting()
    {
        const string markdown = """
            # Heading

            A **bold** and *italic* paragraph with `code`.

            > quoted

            - one
            - two

            ---
            """;

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            Assert.False(result.UsedPlainTextFallback);
            Assert.Equal(markdown, result.RawText);
            Assert.Contains(result.Document.Blocks, block => block is Paragraph { FontWeight: { } weight } && weight == System.Windows.FontWeights.SemiBold);
            Assert.Contains(result.Document.Blocks, block => block is Section);
            Assert.Contains(result.Document.Blocks, block => block is System.Windows.Documents.List);
            Assert.Contains(result.Document.Blocks, block => block is BlockUIContainer);
            var text = new TextRange(result.Document.ContentStart, result.Document.ContentEnd).Text;
            Assert.Contains("Heading", text);
            Assert.Contains("bold", text);
            Assert.Contains("quoted", text);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_RendersPipeTable()
    {
        const string markdown = "| Name | Value |\n| --- | --- |\n| alpha | 1 |";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            var table = Assert.IsType<Table>(Assert.Single(result.Document.Blocks));
            Assert.Equal(2, table.Columns.Count);
            Assert.Equal(2, Assert.Single(table.RowGroups).Rows.Count);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_ExposesOriginalFencedCodeForIndependentCopy()
    {
        const string markdown = "```csharp\nConsole.WriteLine(\"hello\");\n```";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            var code = Assert.Single(result.CodeBlocks);
            Assert.Equal("csharp", code.Language);
            Assert.Contains("Console.WriteLine", code.Code);
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(result.Document.Blocks));
            var border = Assert.IsType<Border>(container.Child);
            Assert.Same(code, border.Tag);
            var panel = Assert.IsType<DockPanel>(border.Child);
            var header = Assert.IsType<Grid>(panel.Children[0]);
            var languageLabel = Assert.IsType<TextBlock>(header.Children[0]);
            Assert.Equal("csharp", languageLabel.Text);
            var copyButton = Assert.IsType<Button>(header.Children[1]);
            Assert.Equal("\u29C9", copyButton.Content);
            Assert.Same(code, copyButton.Tag);
            Assert.False(copyButton.Focusable);
            Assert.False(copyButton.IsTabStop);
            return true;
        });
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a", true)]
    [InlineData("file:///c:/secret.txt", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative", false)]
    public void IsSafeLink_AllowsOnlyAbsoluteHttpAndHttps(string value, bool expected)
    {
        Assert.Equal(expected, MarkdownRenderer.IsSafeLink(value, out _));
    }

    [Fact]
    public void RenderDetailed_LeavesUnsafeLinkTextButDoesNotCreateHyperlink()
    {
        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed("[visible](file:///c:/secret.txt)");
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(result.Document.Blocks));
            Assert.DoesNotContain(paragraph.Inlines, inline => inline is Hyperlink);
            Assert.Contains("visible", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_DoesNotRenderImagesOrRawHtmlElements()
    {
        const string markdown = "before ![secret](https://example.com/image.png) after\n\n<script>alert(1)</script>";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            Assert.DoesNotContain(result.Document.Blocks.OfType<BlockUIContainer>(), block => block.Child is Image);
            var text = new TextRange(result.Document.ContentStart, result.Document.ContentEnd).Text;
            Assert.DoesNotContain("secret", text);
            Assert.Contains("alert(1)", text); // DisableHtml leaves raw HTML as inert text, never executable WPF content.
            return true;
        });
    }

    [Fact]
    public void TryRender_PreservesCompleteRawTextContract()
    {
        const string markdown = "full **source**";

        MarkdownRenderResult? result = null;
        var succeeded = RunInSta(() => MarkdownRenderer.TryRender(markdown, out result));

        Assert.True(succeeded);
        Assert.Equal(markdown, result!.RawText);
        Assert.Null(result.Error);
    }

    [Fact]
    public void RenderDetailed_CollapsesOnlyAtCompleteTopLevelBlockBoundaries()
    {
        var firstBlock = new string('a', 40);
        var fencedCode = "```csharp\n" + new string('b', 80) + "\n```";
        var markdown = firstBlock + "\n\n" + fencedCode;

        var result = RunInSta(() => MarkdownRenderer.RenderDetailed(markdown, 50));

        Assert.True(result.IsCollapsed);
        Assert.Equal(firstBlock, result.DisplayedRawText);
        Assert.Equal(markdown, result.RawText);
        Assert.Empty(result.CodeBlocks);
    }

    [Fact]
    public void RenderDetailed_DoesNotTruncateAnOversizedFirstBlock()
    {
        var markdown = new string('a', 100);

        var result = RunInSta(() => MarkdownRenderer.RenderDetailed(markdown, 50));

        Assert.False(result.IsCollapsed);
        Assert.Equal(markdown, result.DisplayedRawText);
    }

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
            throw new Xunit.Sdk.XunitException(exception.ToString());
        return result!;
    }
}
