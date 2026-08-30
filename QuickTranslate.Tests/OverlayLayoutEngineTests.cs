using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OverlayLayoutEngineTests
{
    [Fact]
    public void Layout_LongChineseTranslation_RemainsFullyContained()
    {
        var result = Layout(320, 160, Item("u1", new OcrBounds(20, 20, 100, 20),
            "这是一个需要在窄框中自动换行的中文译文，并且不能因为框太小而丢失任何内容"));

        var item = Assert.Single(result.Items);
        Assert.NotEqual(ScreenshotOverlayLayoutStatus.Skipped, item.Status);
        Assert.True(item.IsTextFullyContained);
        Assert.True(item.LineCount > 1);
        Assert.InRange(item.LayoutBounds.X, 0, 319);
        Assert.InRange(item.LayoutBounds.Y, 0, 159);
    }

    [Fact]
    public void Layout_LongEnglishWord_WrapsWithoutTruncation()
    {
        var result = Layout(260, 120, Item("u1", new OcrBounds(10, 10, 70, 20),
            "SupercalifragilisticexpialidociousSupercalifragilisticexpialidocious"));

        var item = Assert.Single(result.Items);
        Assert.NotEqual(ScreenshotOverlayLayoutStatus.Skipped, item.Status);
        Assert.True(item.IsTextFullyContained);
        Assert.True(item.LineCount > 1);
    }

    [Fact]
    public void Layout_RespectsConfiguredFontBounds()
    {
        var engine = new OverlayLayoutEngine(new ScreenshotOverlayLayoutOptions(
            MinFontSize: 7,
            MaxFontSize: 13,
            PreferredFontSizeRatio: 1,
            FontSizeStep: 0.5));

        var result = engine.Layout(200, 100, new[]
        {
            Item("small", new OcrBounds(10, 10, 20, 12), "长文本长文本"),
            Item("large", new OcrBounds(80, 10, 100, 80), "OK")
        });

        foreach (var item in result.Items.Where(item => item.Status != ScreenshotOverlayLayoutStatus.Skipped))
            Assert.InRange(item.FontSize, 7, 13);
    }

    [Fact]
    public void Layout_LargeFontStepStillTriesMinimumSize()
    {
        var engine = new OverlayLayoutEngine(new ScreenshotOverlayLayoutOptions(
            MinFontSize: 8,
            MaxFontSize: 24,
            PreferredFontSizeRatio: 1,
            FontSizeStep: 100));

        var item = Assert.Single(engine.Layout(120, 60,
            new[] { Item("u1", new OcrBounds(10, 10, 40, 20), "需要缩小字号才能完整显示的译文") }).Items);

        Assert.NotEqual(ScreenshotOverlayLayoutStatus.Skipped, item.Status);
        Assert.Equal(8, item.FontSize);
        Assert.True(item.IsTextFullyContained);
    }

    [Fact]
    public void Layout_LargeOriginalBox_IsPlaced()
    {
        var item = Assert.Single(Layout(300, 200, Item("u1", new OcrBounds(30, 40, 120, 50), "OK")).Items);

        Assert.Equal(ScreenshotOverlayLayoutStatus.Placed, item.Status);
        Assert.Equal(new OcrBounds(30, 40, 120, 50), item.LayoutBounds);
        Assert.True(item.IsTextFullyContained);
    }

    [Fact]
    public void Layout_SmallOriginalBox_ExpandsOrReducesAsDegraded()
    {
        var item = Assert.Single(Layout(200, 100, Item("u1", new OcrBounds(10, 10, 8, 8), "译文")).Items);

        Assert.Equal(ScreenshotOverlayLayoutStatus.Degraded, item.Status);
        Assert.True(item.IsTextFullyContained);
        Assert.NotEqual(item.SourceBounds, item.LayoutBounds);
    }

    [Fact]
    public void Layout_OverlappingBlocks_MovesSecondBlockDeterministically()
    {
        var input = new[]
        {
            Item("a", new OcrBounds(20, 20, 70, 30), "first"),
            Item("b", new OcrBounds(50, 25, 70, 30), "second")
        };

        var first = new OverlayLayoutEngine().Layout(240, 120, input);
        var second = new OverlayLayoutEngine().Layout(240, 120, input.Reverse().ToArray());

        Assert.Equal(
            first.Items.OrderBy(item => item.UnitId).Select(item => (item.UnitId, item.LayoutBounds, item.Status)),
            second.Items.OrderBy(item => item.UnitId).Select(item => (item.UnitId, item.LayoutBounds, item.Status)));
        Assert.All(first.Items, item => Assert.True(item.IsTextFullyContained));
        Assert.NotEqual(first.Items[0].LayoutBounds, first.Items[1].LayoutBounds);
    }

    [Fact]
    public void Layout_CompletelyOverlappingBlocks_DoesNotThrow()
    {
        var exception = Record.Exception(() => Layout(180, 100,
            Item("a", new OcrBounds(30, 20, 60, 30), "A"),
            Item("b", new OcrBounds(30, 20, 60, 30), "B")));

        Assert.Null(exception);
    }

    [Fact]
    public void Layout_WhenCollisionCannotBeAvoided_ReturnsDegraded()
    {
        var result = Layout(60, 40,
            Item("a", new OcrBounds(0, 0, 60, 40), "A"),
            Item("b", new OcrBounds(0, 0, 60, 40), "B"));

        Assert.Equal(ScreenshotOverlayLayoutStatus.Placed, result.Items[0].Status);
        Assert.Equal(ScreenshotOverlayLayoutStatus.Degraded, result.Items[1].Status);
        Assert.Contains("collision_unavoidable", result.Items[1].DegradationReason);
        Assert.True(result.Items[1].IsTextFullyContained);
    }

    [Fact]
    public void Layout_NeverLeavesImageBounds()
    {
        var result = Layout(100, 80,
            Item("edge", new OcrBounds(80, 65, 20, 15), "这是右下角的长译文，需要向可用区域布局"));

        var item = Assert.Single(result.Items);
        Assert.NotEqual(ScreenshotOverlayLayoutStatus.Skipped, item.Status);
        Assert.True(item.LayoutBounds.IsWithin(100, 80));
    }

    [Fact]
    public void Layout_InvalidBoundsAndEmptyTranslation_AreSkipped()
    {
        var result = Layout(100, 80,
            Item("invalid", new OcrBounds(-1, 2, 20, 20), "text"),
            Item("empty", new OcrBounds(2, 2, 20, 20), "   "));

        Assert.All(result.Items, item => Assert.Equal(ScreenshotOverlayLayoutStatus.Skipped, item.Status));
        Assert.All(result.Items, item => Assert.False(item.IsTextFullyContained));
    }

    [Fact]
    public void Layout_InputOrderDoesNotChangeOutput()
    {
        var items = new[]
        {
            Item("u2", new OcrBounds(90, 10, 60, 24), "two"),
            Item("u1", new OcrBounds(10, 10, 60, 24), "one"),
            Item("u3", new OcrBounds(40, 60, 80, 24), "three")
        };

        var ordered = new OverlayLayoutEngine().Layout(220, 120, items);
        var shuffled = new OverlayLayoutEngine().Layout(220, 120, new[] { items[2], items[0], items[1] });

        Assert.Equal(
            ordered.Items.OrderBy(item => item.UnitId).Select(Snapshot),
            shuffled.Items.OrderBy(item => item.UnitId).Select(Snapshot));
    }

    [Fact]
    public void Layout_UsesLocalCoordinatesForNegativeDisplayOrigin()
    {
        // The global monitor origin is intentionally not part of this engine's
        // contract. A screenshot captured on a negative-origin monitor still
        // uses non-negative coordinates relative to its own top-left corner.
        var item = Assert.Single(Layout(120, 80, Item("u1", new OcrBounds(0, 0, 40, 24), "local")).Items);

        Assert.True(item.LayoutBounds.IsWithin(120, 80));
        Assert.Equal(0, item.LayoutBounds.X);
        Assert.Equal(0, item.LayoutBounds.Y);
    }

    [Fact]
    public void Layout_ReportsMeasuredContainmentConsistently()
    {
        var result = Layout(240, 120,
            Item("fit", new OcrBounds(10, 10, 120, 40), "fit"),
            Item("skip", new OcrBounds(0, 0, 0, 0), "skip"));

        Assert.True(result.Items[0].IsTextFullyContained);
        Assert.False(result.Items[1].IsTextFullyContained);
        Assert.True(result.Items[0].MeasuredTextWidth <= result.Items[0].LayoutBounds.Width + 0.01);
        Assert.True(result.Items[0].MeasuredTextHeight <= result.Items[0].LayoutBounds.Height + 0.01);
    }

    private static ScreenshotOverlayLayoutResult Layout(int width, int height, params ScreenshotOverlayItem[] items) =>
        new OverlayLayoutEngine().Layout(width, height, items);

    private static ScreenshotOverlayItem Item(string id, OcrBounds bounds, string translation) =>
        new(bounds, translation, UnitId: id);

    private static object Snapshot(ScreenshotOverlayLayout item) =>
        (item.UnitId, item.LayoutBounds, item.FontSize, item.LineCount, item.Status, item.DegradationReason);
}
