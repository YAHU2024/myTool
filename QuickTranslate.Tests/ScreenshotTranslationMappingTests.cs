using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationMappingTests
{
    [Fact]
    public void CreateUnits_UsesDeterministicUnitIdsAndCarriesGeometry()
    {
        var paragraphs = OcrBlockAggregator.Aggregate(new[]
        {
            new OcrTextBlock("b0001", "one", new OcrBounds(10, 20, 30, 10)),
            new OcrTextBlock("b0002", "two", new OcrBounds(10, 60, 30, 10))
        });

        var units = ScreenshotTranslationMapper.CreateUnits(paragraphs);

        Assert.Equal(new[] { "u0001", "u0002" }, units.Select(unit => unit.UnitId));
        Assert.Equal(paragraphs[0].Bounds, units[0].Bounds);
    }

    [Theory]
    [InlineData("valid", true, "ok")]
    [InlineData("reordered", true, "ok")]
    [InlineData("missing", false, "missing_id")]
    [InlineData("duplicate", false, "duplicate_id")]
    [InlineData("extra", false, "unexpected_id")]
    public void ParseAndMap_RequiresExactUnitIdSet(string name, bool accepted, string reason)
    {
        var expected = ExpectedUnits();
        var json = name switch
        {
            "valid" => "{\"units\":[{\"id\":\"u0001\",\"translation\":\"一\"},{\"id\":\"u0002\",\"translation\":\"二\"}]}",
            "reordered" => "{\"units\":[{\"id\":\"u0002\",\"translation\":\"二\"},{\"id\":\"u0001\",\"translation\":\"一\"}]}",
            "missing" => "{\"units\":[{\"id\":\"u0001\",\"translation\":\"一\"}]}",
            "duplicate" => "{\"units\":[{\"id\":\"u0001\",\"translation\":\"一\"},{\"id\":\"u0001\",\"translation\":\"壹\"}]} ",
            "extra" => "{\"units\":[{\"id\":\"u0001\",\"translation\":\"一\"},{\"id\":\"u0002\",\"translation\":\"二\"},{\"id\":\"u9999\",\"translation\":\"未知\"}]} ",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        var result = ScreenshotTranslationMapper.ParseAndMap(json, expected);

        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(reason, result.Reason);
        if (accepted)
            Assert.Equal(new[] { "u0001", "u0002" }, result.MappedUnits.Select(unit => unit.UnitId));
    }

    [Fact]
    public void ParseAndMap_RejectsPlainTextAndEmptyTranslation()
    {
        var expected = ExpectedUnits();

        Assert.Equal("invalid_json", ScreenshotTranslationMapper.ParseAndMap("一\n二", expected).Reason);
        Assert.Equal(
            "empty_translation",
            ScreenshotTranslationMapper.ParseAndMap(
                "{\"units\":[{\"id\":\"u0001\",\"translation\":\"\"},{\"id\":\"u0002\",\"translation\":\"二\"}]}",
                expected).Reason);
        Assert.Equal("missing_units", ScreenshotTranslationMapper.ParseAndMap("null", expected).Reason);
    }

    private static IReadOnlyList<ScreenshotTranslationUnit> ExpectedUnits() => new[]
    {
        new ScreenshotTranslationUnit("u0001", "one", Array.Empty<OcrTextBlock>(), new OcrBounds(0, 0, 10, 10)),
        new ScreenshotTranslationUnit("u0002", "two", Array.Empty<OcrTextBlock>(), new OcrBounds(0, 20, 10, 10))
    };
}
