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
    public void RenderDetailed_UsesTheStreamingConversationFontForBodyText()
    {
        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed("简体中文正文");
            Assert.Equal(MarkdownRenderer.ConversationFontFamilyName, result.Document.FontFamily.Source);
            Assert.Equal(MarkdownRenderer.ConversationFontSize, result.Document.FontSize);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_AllowsFollowUpBodyScale()
    {
        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed("# 标题\n\n正文", fontSize: MarkdownRenderer.AnalysisConversationFontSize);
            Assert.Equal(15, result.Document.FontSize);
            var heading = Assert.IsType<Paragraph>(result.Document.Blocks.First());
            Assert.Equal(20.25, heading.FontSize);
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
            var codeHost = Assert.IsType<RichTextBox>(panel.Children[1]);
            Assert.True(codeHost.IsReadOnly);
            Assert.True(codeHost.Focusable);
            Assert.False(codeHost.IsTabStop);
            Assert.Equal(code.Code, new TextRange(codeHost.Document.ContentStart, codeHost.Document.ContentEnd).Text.TrimEnd('\r', '\n'));
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

    [Theory]
    [InlineData("# streaming head")]
    [InlineData("paragraph with **unfinished emphasis")]
    [InlineData("- first\n- second in progress")]
    [InlineData("[link text](https://example.com/incomplete")]
    public void TryRender_AcceptsIncompleteStreamingPrefixes(string markdown)
    {
        MarkdownRenderResult? result = null;
        var succeeded = RunInSta(() => MarkdownRenderer.TryRender(markdown, out result));

        Assert.True(succeeded);
        Assert.False(result!.UsedPlainTextFallback);
        Assert.Equal(markdown, result.RawText);
        Assert.NotEmpty(result.Document.Blocks);
    }

    [Fact]
    public void RenderDetailed_UnclosedStreamingFenceRemainsCopyable()
    {
        const string markdown = "```csharp\nConsole.WriteLine(\"streaming\");";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            var code = Assert.Single(result.CodeBlocks);
            Assert.Equal("csharp", code.Language);
            Assert.Contains("Console.WriteLine", code.Code);
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(result.Document.Blocks));
            var border = Assert.IsType<Border>(container.Child);
            var panel = Assert.IsType<DockPanel>(border.Child);
            var codeText = Assert.IsType<RichTextBox>(panel.Children[1]);
            Assert.Equal(
                code.Code,
                new TextRange(codeText.Document.ContentStart, codeText.Document.ContentEnd).Text.TrimEnd('\r', '\n'));
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_ClosedFenceAppliesSyntaxHighlighting()
    {
        const string markdown = "```csharp\nusing System;\nreturn true;\n```";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown);
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(result.Document.Blocks));
            var border = Assert.IsType<Border>(container.Child);
            var panel = Assert.IsType<DockPanel>(border.Child);
            var codeText = Assert.IsType<RichTextBox>(panel.Children[1]);
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(codeText.Document.Blocks));
            Assert.True(paragraph.Inlines.OfType<Run>().Count() > 1);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_FinalUnclosedFenceDoesNotSwallowPlainTextAsCode()
    {
        const string markdown = "before\n\n```\n普通文本\n## 标题";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown, isFinal: true);

            Assert.Contains(result.Document.Blocks, block => block is Paragraph paragraph &&
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("普通文本"));
            Assert.DoesNotContain(result.Document.Blocks, block => block is BlockUIContainer);
            Assert.Equal(markdown, result.RawText);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_FinalNestedFenceCollisionKeepsFollowingProseOutOfCodeBlock()
    {
        const string markdown = "```markdown\n```python\nprint(\"Hello\")\n```\n```\n然后这些工具会自动高亮代码。\n## 后续标题";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown, isFinal: true);
            var documentText = new TextRange(result.Document.ContentStart, result.Document.ContentEnd).Text;

            Assert.Single(result.CodeBlocks);
            Assert.Single(result.Document.Blocks.OfType<BlockUIContainer>());
            Assert.Contains("然后这些工具会自动高亮代码。", documentText);
            Assert.Equal(markdown, result.RawText);
            return true;
        });
    }

    [Fact]
    public void RenderDetailed_NormalizesFenceInfoLanguageBeforeHighlighting()
    {
        const string markdown = "```python title=demo\ndef greet(name):\n    return name\n```";

        RunInSta(() =>
        {
            var result = MarkdownRenderer.RenderDetailed(markdown, isFinal: true);
            var code = Assert.Single(result.CodeBlocks);
            Assert.Equal("python", code.Language);
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(result.Document.Blocks));
            var panel = Assert.IsType<DockPanel>(Assert.IsType<Border>(container.Child).Child);
            var codeText = Assert.IsType<RichTextBox>(panel.Children[1]);
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(codeText.Document.Blocks));
            Assert.True(paragraph.Inlines.OfType<Run>().Count() > 1);
            return true;
        });
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
