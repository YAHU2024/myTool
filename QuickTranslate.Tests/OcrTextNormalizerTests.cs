using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrTextNormalizerTests
{
    [Theory]
    [InlineData("本 地 截 图 翻 译", "本地截图翻译")]
    [InlineData("QuickTransIate   OCR\tSpike 123", "QuickTransIate OCR Spike 123")]
    [InlineData("你好 ， 世界 ！", "你好，世界！")]
    [InlineData("  hello  world  ", "hello world")]
    public void Normalize_RemovesOcrNoiseButPreservesLatinWordSeparators(string input, string expected)
    {
        Assert.Equal(expected, OcrTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Join_DropsEmptyItemsAndUsesLineSeparator()
    {
        var result = OcrTextNormalizer.Join(new[] { "本 地", " ", "Hello  world" });

        Assert.Equal("本地\nHello world", result);
    }
}
