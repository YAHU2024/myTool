using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationStreamParserTests
{
    [Fact]
    public void Append_DoesNotPublishUntilSplitJsonObjectIsComplete()
    {
        var parser = new ScreenshotTranslationStreamParser(new[] { "u0001" });

        Assert.Empty(parser.Append("{\"id\":\"u0001\",\"trans"));
        var emitted = parser.Append("lation\":\"你好\"}" );

        var translated = Assert.Single(emitted);
        Assert.Equal("u0001", translated.UnitId);
        Assert.Equal("你好", translated.Translation);
    }

    [Fact]
    public void Append_AcceptsOutOfOrderUnits_AndCompleteMapsById()
    {
        var parser = new ScreenshotTranslationStreamParser(new[] { "u0001", "u0002" });

        var first = parser.Append("{\"id\":\"u0002\",\"translation\":\"二\"}\n");
        var second = parser.Append("{\"id\":\"u0001\",\"translation\":\"一\"}");
        var result = parser.Complete(Units());

        Assert.Equal("u0002", Assert.Single(first).UnitId);
        Assert.Equal("u0001", Assert.Single(second).UnitId);
        Assert.True(result.Accepted);
        Assert.Equal(new[] { "u0001", "u0002" }, result.MappedUnits.Select(unit => unit.UnitId));
    }

    [Theory]
    [InlineData("duplicate_id", "{\"id\":\"u0001\",\"translation\":\"一\"}{\"id\":\"u0001\",\"translation\":\"壹\"}")]
    [InlineData("unexpected_id", "{\"id\":\"u9999\",\"translation\":\"未知\"}")]
    public void Append_RejectsDuplicateOrUnknownIds(string reason, string content)
    {
        var parser = new ScreenshotTranslationStreamParser(new[] { "u0001" });

        parser.Append(content);
        var result = parser.Complete(Units("u0001"));

        Assert.False(result.Accepted);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Complete_ReportsMissingAndIncompleteUnits()
    {
        var parser = new ScreenshotTranslationStreamParser(new[] { "u0001", "u0002" });
        parser.Append("{\"id\":\"u0001\",\"translation\":\"一\"}");

        var result = parser.Complete(Units());

        Assert.False(result.Accepted);
        Assert.Equal("missing_id", result.Reason);

        var incomplete = new ScreenshotTranslationStreamParser(new[] { "u0001" });
        incomplete.Append("{\"id\":\"u0001\"");
        var incompleteResult = incomplete.Complete(Units("u0001"));
        Assert.False(incompleteResult.Accepted);
        Assert.Equal("incomplete_json", incompleteResult.Reason);
    }

    [Fact]
    public void Append_AcceptsProviderWrapperForCompatibility()
    {
        var parser = new ScreenshotTranslationStreamParser(new[] { "u0001", "u0002" });
        parser.Append("{\"units\":[{\"id\":\"u0002\",\"translation\":\"二\"},{\"id\":\"u0001\",\"translation\":\"一\"}]}");

        var result = parser.Complete(Units());

        Assert.True(result.Accepted);
        Assert.Equal(new[] { "一", "二" }, result.MappedUnits.Select(unit => unit.Translation));
    }

    private static IReadOnlyList<ScreenshotTranslationUnit> Units(params string[] ids) =>
        (ids.Length == 0 ? new[] { "u0001", "u0002" } : ids)
        .Select((id, index) => new ScreenshotTranslationUnit(
            id,
            $"source-{index}",
            Array.Empty<OcrTextBlock>(),
            new OcrBounds(0, index * 20, 100, 20)))
        .ToArray();
}
