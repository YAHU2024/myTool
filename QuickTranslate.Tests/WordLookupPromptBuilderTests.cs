using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class WordLookupPromptBuilderTests
{
    [Fact]
    public void NormalizeQuery_TrimsAndCollapsesWhitespace()
    {
        Assert.Equal("look up", WordLookupPromptBuilder.NormalizeQuery("  look\t  up  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("one\ntwo")]
    public void NormalizeQuery_RejectsInvalidInput(string input)
    {
        Assert.Throws<ArgumentException>(() => WordLookupPromptBuilder.NormalizeQuery(input));
    }

    [Fact]
    public void NormalizeQuery_CountsUnicodeScalarsInsteadOfUtf16Units()
    {
        var valid = string.Concat(Enumerable.Repeat("😀", 128));
        var invalid = valid + "a";

        Assert.Equal(valid, WordLookupPromptBuilder.NormalizeQuery(valid));
        Assert.Throws<ArgumentException>(() => WordLookupPromptBuilder.NormalizeQuery(invalid));
    }

    [Fact]
    public void Build_RequiresJsonAndExcludesCefr()
    {
        var prompt = WordLookupPromptBuilder.Build("简体中文");

        Assert.Contains("JSON only", prompt);
        Assert.Contains("not_found", prompt);
        Assert.Contains("Do not include CEFR", prompt);
    }
}
