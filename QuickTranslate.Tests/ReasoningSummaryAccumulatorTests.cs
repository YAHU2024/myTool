using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ReasoningSummaryAccumulatorTests
{
    [Fact]
    public void Append_StopsAtUnicodeScalarLimitWithoutSplittingSurrogatePair()
    {
        var accumulator = new ReasoningSummaryAccumulator();
        var prefix = new string('a', ReasoningSummaryAccumulator.MaxRunes - 1);

        var accepted = accumulator.Append(prefix + "😀b");

        Assert.Equal(prefix + "😀", accepted);
        Assert.Equal(prefix + "😀", accumulator.Snapshot());
        Assert.True(accumulator.IsTruncated);
    }

    [Fact]
    public void Append_MarksLaterOverflowWithoutChangingSnapshot()
    {
        var accumulator = new ReasoningSummaryAccumulator();
        var full = new string('x', ReasoningSummaryAccumulator.MaxRunes);

        Assert.Equal(full, accumulator.Append(full));
        Assert.False(accumulator.IsTruncated);

        Assert.Empty(accumulator.Append("overflow"));
        Assert.True(accumulator.IsTruncated);
        Assert.Equal(full, accumulator.Snapshot());
    }
}
