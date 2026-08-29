using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class RecentSelectionCopyEvaluatorTests
{
    private const long SeqAtMouseDown = 100;

    [Fact]
    public void Suspects_WhenSequenceChangedShortlyAfterSelection()
    {
        // 鼠标按下 2 秒、抬起 1 秒后剪贴板序列号变化：典型的选中即复制。
        Assert.True(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 1_000,
            mouseUpTick: 2_000,
            currentSequence: 117,
            nowTick: 3_000));
    }

    [Fact]
    public void Skips_WhenBaselineMissing()
    {
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            -1,
            mouseDownTick: 1_000,
            mouseUpTick: 2_000,
            currentSequence: 117,
            nowTick: 3_000));
    }

    [Fact]
    public void Skips_WhenClipboardUnchangedSinceMouseDown()
    {
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 1_000,
            mouseUpTick: 2_000,
            currentSequence: SeqAtMouseDown,
            nowTick: 3_000));
    }

    [Fact]
    public void Skips_WhenMouseUpIsStale()
    {
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 1_000,
            mouseUpTick: 2_000,
            currentSequence: 117,
            nowTick: 2_000 + RecentSelectionCopyEvaluator.MouseUpFreshnessMs + 1));
    }

    [Fact]
    public void Skips_WhenMouseDownIsStale()
    {
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 1_000,
            mouseUpTick: 31_000,
            currentSequence: 117,
            nowTick: 31_000 + 1));
    }

    [Fact]
    public void Skips_WhenTicksAreInconsistent()
    {
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 2_000,
            mouseUpTick: 1_000,
            currentSequence: 117,
            nowTick: 3_000));
        Assert.False(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: -1,
            mouseUpTick: 2_000,
            currentSequence: 117,
            nowTick: 3_000));
    }

    [Fact]
    public void Suspects_AtFreshnessBoundary()
    {
        Assert.True(RecentSelectionCopyEvaluator.IsAutoCopySuspected(
            SeqAtMouseDown,
            mouseDownTick: 0,
            mouseUpTick: 1_000,
            currentSequence: 117,
            nowTick: 1_000 + RecentSelectionCopyEvaluator.MouseUpFreshnessMs));
    }
}
