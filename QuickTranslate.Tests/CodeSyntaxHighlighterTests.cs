using System.Windows.Controls;
using System.Windows.Documents;
using QuickTranslate.Helpers;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class CodeSyntaxHighlighterTests
{
    [Fact]
    public void TryHighlight_CSharpCreatesStyledRunsAndPreservesText()
    {
        RunInSta(() =>
        {
            var target = new TextBlock();
            const string code = "using System;\nreturn true;";

            Assert.True(CodeSyntaxHighlighter.TryHighlight(target, code, "csharp"));
            Assert.Equal(code, new TextRange(target.ContentStart, target.ContentEnd).Text.TrimEnd('\r', '\n'));
            Assert.Contains(target.Inlines.OfType<Run>(), run => run.Foreground is not null);
            Assert.True(target.Inlines.OfType<Run>().Count() > 1);
            return true;
        });
    }

    [Theory]
    [InlineData("js")]
    [InlineData("json")]
    [InlineData("py")]
    [InlineData("sql")]
    public void TryHighlight_ResolvesSupportedAliases(string language)
    {
        RunInSta(() =>
        {
            var target = new TextBlock();
            Assert.True(CodeSyntaxHighlighter.TryHighlight(target, "const value = 1;", language));
            return true;
        });
    }

    [Fact]
    public void TryHighlight_ReturnsFalseForUnsupportedLanguage()
    {
        RunInSta(() =>
        {
            var target = new TextBlock();
            Assert.False(CodeSyntaxHighlighter.TryHighlight(target, "key: value", "yaml"));
            return true;
        });
    }

    [Fact]
    public void TryHighlight_SkipsOversizedCodeBlocks()
    {
        RunInSta(() =>
        {
            var target = new TextBlock();
            var code = new string('x', CodeSyntaxHighlighter.MaxHighlightedCharacters + 1);

            Assert.False(CodeSyntaxHighlighter.TryHighlight(target, code, "csharp"));
            Assert.Empty(target.Inlines);
            return true;
        });
    }

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
            throw new Xunit.Sdk.XunitException(exception.ToString());
        return result!;
    }
}
