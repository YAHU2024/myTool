using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class GlobalKeyboardHookTests
{
    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public void MatchesModifiers_ExactAltSRejectsMissingAndAdditionalModifiers(
        bool alt, bool ctrl, bool shift, bool windows, bool expected)
    {
        using var hook = new GlobalKeyboardHook
        {
            HotKey = 0x53,
            RequireAlt = true,
            RequireCtrl = false,
            RequireShift = false,
            RequireExactModifiers = true
        };

        Assert.Equal(expected, hook.MatchesModifiers(alt, ctrl, shift, windows));
    }

    [Theory]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(false, true, true, true, false)]
    public void MatchesModifiers_ExactCustomCombinationRequiresAllConfiguredModifiers(
        bool alt, bool ctrl, bool shift, bool windows, bool expected)
    {
        using var hook = new GlobalKeyboardHook
        {
            RequireAlt = false,
            RequireCtrl = true,
            RequireShift = true,
            RequireExactModifiers = true
        };

        Assert.Equal(expected, hook.MatchesModifiers(alt, ctrl, shift, windows));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, false, false)]
    public void MatchesModifiers_LegacyModeContinuesAcceptingAdditionalModifiers(
        bool alt, bool ctrl, bool shift, bool windows, bool expected)
    {
        using var hook = new GlobalKeyboardHook
        {
            RequireAlt = true,
            RequireCtrl = false,
            RequireShift = false
        };

        Assert.False(hook.RequireExactModifiers);
        Assert.Equal(expected, hook.MatchesModifiers(alt, ctrl, shift, windows));
    }
}
