using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrImageTests
{
    [Fact]
    public void Validate_AllowsStridePaddingAndExactPayload()
    {
        var image = new OcrImage(3, 2, 16, new byte[32]);

        image.Validate();
        image.Validate(OcrResourceLimits.Default);
    }

    [Theory]
    [InlineData(0, 10, 40, 400)]
    [InlineData(10, 0, 40, 400)]
    [InlineData(10, 10, 39, 390)]
    public void Validate_RejectsInvalidDimensionsStrideOrPayload(
        int width,
        int height,
        int stride,
        int payloadLength)
    {
        var image = new OcrImage(width, height, stride, new byte[Math.Max(0, payloadLength)]);

        Assert.Throws<ArgumentException>(() => image.Validate());
    }

    [Fact]
    public void Validate_RejectsIntegerOverflowInRequiredPayload()
    {
        var image = new OcrImage(int.MaxValue / 4 + 1, 2, int.MaxValue, ReadOnlyMemory<byte>.Empty);

        Assert.Throws<ArgumentException>(() => image.Validate());
    }

    [Fact]
    public void ResourceLimits_RejectOversizedImageWithoutTruncating()
    {
        var image = new OcrImage(5, 5, 20, new byte[100]);
        var limits = new OcrResourceLimits(MaxPixelCount: 24);

        Assert.Throws<ArgumentException>(() => image.Validate(limits));
    }
}
