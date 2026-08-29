using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrBlockValidationTests
{
    [Fact]
    public void Validate_RejectsBlankText()
    {
        var block = new OcrTextBlock("b0001", " ", new OcrBounds(0, 0, 10, 10));

        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(block, 100, 100));
    }

    [Fact]
    public void Validate_RejectsNegativeOrOutOfRangeBounds()
    {
        var negative = new OcrTextBlock("b0001", "text", new OcrBounds(-1, 0, 10, 10));
        var outside = new OcrTextBlock("b0002", "text", new OcrBounds(95, 0, 10, 10));

        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(negative, 100, 100));
        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(outside, 100, 100));
    }

    [Fact]
    public void ValidateAll_RejectsDuplicateBlockIds()
    {
        var blocks = new[]
        {
            new OcrTextBlock("b0001", "one", new OcrBounds(0, 0, 20, 10)),
            new OcrTextBlock("b0001", "two", new OcrBounds(0, 20, 20, 10))
        };

        Assert.Throws<ArgumentException>(() => OcrBlockValidator.ValidateAll(blocks, 100, 100));
    }

    [Fact]
    public void Validate_AllowsMissingConfidenceButRejectsInvalidConfidence()
    {
        var noConfidence = new OcrTextBlock("b0001", "text", new OcrBounds(0, 0, 20, 10));
        var invalid = noConfidence with { Confidence = 1.1 };

        OcrBlockValidator.Validate(noConfidence, 100, 100);
        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(invalid, 100, 100));
    }

    [Fact]
    public void Validate_AcceptsPolygonAndOrientation()
    {
        var block = new OcrTextBlock(
            "b0001",
            "text",
            new OcrBounds(10, 10, 80, 30),
            0.92,
            new OcrPoint[]
            {
                new(10, 12), new(88, 10), new(90, 38), new(12, 40)
            },
            2.5);

        OcrBlockValidator.Validate(block, 100, 100);
    }

    [Fact]
    public void Validate_RejectsInvalidPolygonGeometry()
    {
        var tooFewPoints = new OcrTextBlock(
            "b0001",
            "text",
            new OcrBounds(0, 0, 20, 10),
            Polygon: new OcrPoint[] { new(0, 0), new(20, 0), new(20, 10) });
        var outOfRange = tooFewPoints with
        {
            Polygon = new OcrPoint[]
            {
                new(0, 0), new(20, 0), new(20, 10), new(-1, 10)
            }
        };
        var zeroArea = tooFewPoints with
        {
            Polygon = new OcrPoint[]
            {
                new(0, 0), new(10, 0), new(20, 0), new(30, 0)
            }
        };

        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(tooFewPoints, 100, 100));
        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(outOfRange, 100, 100));
        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(zeroArea, 100, 100));
    }

    [Fact]
    public void Validate_RejectsInvalidOrientation()
    {
        var block = new OcrTextBlock(
            "b0001",
            "text",
            new OcrBounds(0, 0, 20, 10),
            OrientationDegrees: double.NaN);

        Assert.Throws<ArgumentException>(() => OcrBlockValidator.Validate(block, 100, 100));
    }
}
