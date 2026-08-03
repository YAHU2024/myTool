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

    [Fact]
    public void Format_DoesNotInsertBlankDefinitionBeforeEnglishOnlySense()
    {
        var result = new WordLookupResult(
            "take",
            Array.Empty<WordPronunciation>(),
            [new WordSense("动词", string.Empty, "move or carry something")],
            Array.Empty<WordExample>(),
            Array.Empty<string>(),
            new WordLookupSource("local", "本地词典", WordLookupSourceKind.Dictionary));

        var text = WordLookupTextFormatter.Format(result);

        Assert.Contains("动词 move or carry something", text);
        Assert.DoesNotContain("动词 \r\n", text);
    }
}
