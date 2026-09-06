using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotLanguageRouterTests
{
    [Theory]
    [InlineData("This is a test", ScreenshotSourceLanguage.English)]
    [InlineData("这是一个测试", ScreenshotSourceLanguage.Chinese)]
    [InlineData("これはテストです", ScreenshotSourceLanguage.Japanese)]
    [InlineData("123 !!!", ScreenshotSourceLanguage.Unknown)]
    public void Detect_UsesScriptFeatures(string text, ScreenshotSourceLanguage expected) =>
        Assert.Equal(expected, ScreenshotLanguageRouter.Detect(text));
}
