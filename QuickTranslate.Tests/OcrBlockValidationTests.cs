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
}
