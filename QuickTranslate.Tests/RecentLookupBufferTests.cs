using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class RecentLookupBufferTests
{
    [Fact]
    public void AddSuccessful_DeduplicatesMovesToFrontAndEvicts()
    {
        var buffer = new RecentLookupBuffer(3);
        buffer.AddSuccessful("One");
        buffer.AddSuccessful("Two");
        buffer.AddSuccessful("Three");
        buffer.AddSuccessful("one");
        buffer.AddSuccessful("Four");

        Assert.Equal(["Four", "one", "Three"], buffer.Items);
    }
}
