using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickTranslate.Helpers;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class StreamingMarkdownRendererTests
{
    [Fact]
    public void Update_AttachedReadOnlyHostWithCodeBlock_DoesNotCreateUndoSerialization()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            var host = new RichTextBox
            {
                Document = renderer.Document,
                IsReadOnly = true,
                IsUndoEnabled = false
            };

            Assert.True(renderer.Update("```csharp\nvar value = 1;\n"));
            Assert.True(renderer.Update("```csharp\nvar value = 1;\nConsole.WriteLine(value);\n"));

            Assert.False(host.IsUndoEnabled);
            Assert.Single(renderer.Document.Blocks.OfType<BlockUIContainer>());
            return true;
        });
    }

    [Fact]
    public void Update_PreservesCommittedBlocksWhileReplacingOnlyActiveTail()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            Assert.True(renderer.Update("# Heading\n\nfirst"));
            var heading = renderer.Document.Blocks.FirstBlock;
            var activeParagraph = renderer.Document.Blocks.LastBlock;

            Assert.True(renderer.Update("# Heading\n\nfirst paragraph grows"));

            Assert.Same(heading, renderer.Document.Blocks.FirstBlock);
            Assert.Same(activeParagraph, renderer.Document.Blocks.LastBlock);
            Assert.Equal("Heading\r\nfirst paragraph grows\r\n", DocumentText(renderer.Document));
            Assert.Equal("first paragraph grows".Length, renderer.ActiveCharacterCount);
            Assert.Equal("# Heading\n\n".Length, renderer.CommittedCharacterCount);
            return true;
        });
    }

    [Fact]
    public void Update_PlainTailReusesWpfObjectsWithoutMarkdigReparse()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            Assert.True(renderer.Update("plain"));
            var paragraph = Assert.IsType<Paragraph>(renderer.Document.Blocks.FirstBlock);
            var run = Assert.IsType<Run>(paragraph.Inlines.FirstInline);

            Assert.True(renderer.Update("plain text grows"));

            Assert.Same(paragraph, renderer.Document.Blocks.FirstBlock);
            Assert.Same(run, paragraph.Inlines.FirstInline);
            Assert.Equal("plain text grows", run.Text);
            Assert.Equal(0, renderer.ParsedCharacterCount);
            return true;
        });
    }

    [Fact]
    public void Update_SeparateActiveDocumentKeepsStableLayoutIsolated()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(
                16,
                int.MaxValue,
                separateActiveDocument: true);
            var activeDocument = Assert.IsType<FlowDocument>(renderer.ActiveDocument);

            Assert.True(renderer.Update("# Heading\n\nplain"));
            var stableHeading = renderer.Document.Blocks.FirstBlock;

            Assert.True(renderer.Update("# Heading\n\nplain text grows"));

            Assert.Same(stableHeading, renderer.Document.Blocks.FirstBlock);
            Assert.Equal("Heading\r\n", DocumentText(renderer.Document));
            Assert.Empty(activeDocument.Blocks);
            Assert.Equal("plain text grows", renderer.ActivePlainText);
            Assert.True(renderer.HasStableBlocks);
            Assert.False(renderer.HasActiveBlocks);
            Assert.True(renderer.HasActiveContent);
            return true;
        });
    }

    [Fact]
    public void Update_SeparateActiveDocumentMovesCompletedTailIntoStableDocument()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(
                16,
                int.MaxValue,
                separateActiveDocument: true);
            var activeDocument = Assert.IsType<FlowDocument>(renderer.ActiveDocument);

            Assert.True(renderer.Update("first"));
            Assert.False(renderer.HasStableBlocks);
            Assert.False(renderer.HasActiveBlocks);
            Assert.Equal("first", renderer.ActivePlainText);

            Assert.True(renderer.Update("first\n\nnext"));

            Assert.Equal("first\r\n", DocumentText(renderer.Document));
            Assert.Empty(activeDocument.Blocks);
            Assert.Equal("next", renderer.ActivePlainText);
            Assert.True(renderer.HasStableBlocks);
            Assert.True(renderer.HasActiveContent);
            return true;
        });
    }

    [Fact]
    public void Update_AttachedSeparateActiveHostWithCodeBlock_DoesNotCreateUndoSerialization()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(
                16,
                int.MaxValue,
                separateActiveDocument: true);
            var stableHost = new RichTextBox
            {
                Document = renderer.Document,
                IsReadOnly = true,
                IsUndoEnabled = false
            };
            var activeHost = new RichTextBox
            {
                Document = Assert.IsType<FlowDocument>(renderer.ActiveDocument),
                IsReadOnly = true,
                IsUndoEnabled = false
            };

            Assert.True(renderer.Update("before\n\n```csharp\nvar value = 1;\n"));
            Assert.True(renderer.Update("before\n\n```csharp\nvar value = 1;\nConsole.WriteLine(value);\n"));

            Assert.False(stableHost.IsUndoEnabled);
            Assert.False(activeHost.IsUndoEnabled);
            Assert.Single(activeHost.Document.Blocks.OfType<BlockUIContainer>());
            return true;
        });
    }

    [Fact]
    public void Update_MarkdownSyntaxLeavesPlainTailFastPath()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            Assert.True(renderer.Update("plain"));
            var plainParagraph = renderer.Document.Blocks.FirstBlock;

            Assert.True(renderer.Update("plain **bold**"));

            Assert.NotSame(plainParagraph, renderer.Document.Blocks.FirstBlock);
            Assert.True(renderer.ParsedCharacterCount > 0);
            Assert.Contains(
                Assert.IsType<Paragraph>(renderer.Document.Blocks.FirstBlock).Inlines,
                inline => inline is Span span && span.FontWeight == FontWeights.Bold);
            return true;
        });
    }

    [Theory]
    [InlineData("ordinary text", true)]
    [InlineData("- list item", false)]
    [InlineData("2. ordered item", false)]
    [InlineData("**bold**", false)]
    [InlineData("https://example.com", false)]
    [InlineData("name@example.com", false)]
    [InlineData("x == marked", false)]
    public void IsSimplePlainTextTail_UsesConservativeMarkdownDetection(string source, bool expected)
    {
        Assert.Equal(expected, StreamingMarkdownRenderer.IsSimplePlainTextTail(source));
    }

    [Fact]
    public void Update_DoesNotCommitBlankLinesInsideOpenFence()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);

            Assert.True(renderer.Update("before\n\n```csharp\nvar x = 1;\n\n"));

            Assert.Equal("before\n\n".Length, renderer.CommittedCharacterCount);
            Assert.Equal("```csharp\nvar x = 1;\n\n".Length, renderer.ActiveCharacterCount);

            Assert.True(renderer.Update("before\n\n```csharp\nvar x = 1;\n\n```\n\nafter"));
            Assert.Equal("before\n\n```csharp\nvar x = 1;\n\n```\n\n".Length, renderer.CommittedCharacterCount);
            Assert.Equal("after".Length, renderer.ActiveCharacterCount);
            return true;
        });
    }

    [Fact]
    public void Update_NonAppendSnapshotResetsDocumentState()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            Assert.True(renderer.Update("old heading\n\nold tail"));

            Assert.True(renderer.Update("replacement"));

            Assert.Equal("replacement\r\n", DocumentText(renderer.Document));
            Assert.Equal(0, renderer.CommittedCharacterCount);
            Assert.Equal("replacement".Length, renderer.ActiveCharacterCount);
            return true;
        });
    }

    [Fact]
    public void Update_ManyCompletedBlocksAvoidsFullDocumentReparseWork()
    {
        RunInSta(() =>
        {
            var renderer = new StreamingMarkdownRenderer(16, int.MaxValue);
            var snapshot = string.Empty;
            long fullDocumentParseCharacters = 0;

            for (var index = 0; index < 200; index++)
            {
                snapshot += $"paragraph {index}\n\n";
                fullDocumentParseCharacters += snapshot.Length;
                Assert.True(renderer.Update(snapshot));
            }

            Assert.True(renderer.ParsedCharacterCount < fullDocumentParseCharacters / 10);
            Assert.Equal(snapshot.Length, renderer.CommittedCharacterCount);
            Assert.Equal(0, renderer.ActiveCharacterCount);
            var stats = renderer.GetStats();
            Assert.Equal(200, stats.FrameCount);
            Assert.Equal(renderer.ParsedCharacterCount, stats.ParsedCharacters);
            Assert.True(stats.AllocatedBytes > 0);
            Assert.True(stats.MaxRenderDurationMs >= stats.AverageRenderDurationMs);
            return true;
        });
    }

    [Fact]
    public void FindStablePrefixLength_RequiresCompletedLineAndRespectsFence()
    {
        Assert.Equal(0, StreamingMarkdownRenderer.FindStablePrefixLength("paragraph\n"));
        Assert.Equal(11, StreamingMarkdownRenderer.FindStablePrefixLength("paragraph\n\nnext"));
        Assert.Equal(0, StreamingMarkdownRenderer.FindStablePrefixLength("```\ncode\n\n"));
        Assert.Equal(14, StreamingMarkdownRenderer.FindStablePrefixLength("```\ncode\n```\n\nnext"));
    }

    private static string DocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

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
