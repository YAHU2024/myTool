using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class WordLookupTextFormatterTests
{
    [Fact]
    public void Format_IncludesVisibleStructuredFieldsAndSource()
    {
        var result = new WordLookupResult(
            "resilient",
            [new WordPronunciation("UK", "/rɪˈzɪl.i.ənt/")],
            [new WordSense("adj.", "有韧性的", "able to recover quickly")],
            [new WordExample("Children are resilient.", "孩子们很有韧性。")],
            ["highly resilient"],
            new WordLookupSource("ai", "AI 释义 · model-x", WordLookupSourceKind.AiGenerated));

        var text = WordLookupTextFormatter.Format(result);

        Assert.Contains("resilient", text);
        Assert.Contains("有韧性的", text);
        Assert.Contains("Children are resilient.", text);
        Assert.Contains("highly resilient", text);
        Assert.EndsWith("AI 释义 · model-x", text);
    }
}
