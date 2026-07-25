using System.Text.Json;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TranslationTriggerModeTests
{
    [Theory]
    [InlineData(TranslationTriggerMode.Both, true, true)]
    [InlineData(TranslationTriggerMode.SelectionOnly, true, false)]
    [InlineData(TranslationTriggerMode.HotKeyOnly, false, true)]
    [InlineData(TranslationTriggerMode.Off, false, false)]
    public void TriggerCapabilities_MatchConfiguredMode(
        TranslationTriggerMode mode,
        bool canTriggerSelection,
        bool canTriggerHotKey)
    {
        Assert.Equal(canTriggerSelection, TranslationTriggerModes.CanTriggerSelection(mode));
        Assert.Equal(canTriggerHotKey, TranslationTriggerModes.CanTriggerHotKey(mode));
    }

    [Fact]
    public void Pause_ActiveMode_RemembersItAndTurnsOff()
    {
        var paused = TranslationTriggerModes.Pause(
            TranslationTriggerMode.HotKeyOnly,
            TranslationTriggerMode.Both);

        Assert.Equal(TranslationTriggerMode.Off, paused.Mode);
        Assert.Equal(TranslationTriggerMode.HotKeyOnly, paused.LastActive);
    }

    [Fact]
    public void Pause_AlreadyOff_PreservesLastActiveMode()
    {
        var paused = TranslationTriggerModes.Pause(
            TranslationTriggerMode.Off,
            TranslationTriggerMode.SelectionOnly);

        Assert.Equal(TranslationTriggerMode.Off, paused.Mode);
        Assert.Equal(TranslationTriggerMode.SelectionOnly, paused.LastActive);
    }

    [Theory]
    [InlineData(TranslationTriggerMode.Both, TranslationTriggerMode.Both)]
    [InlineData(TranslationTriggerMode.SelectionOnly, TranslationTriggerMode.SelectionOnly)]
    [InlineData(TranslationTriggerMode.HotKeyOnly, TranslationTriggerMode.HotKeyOnly)]
    [InlineData(TranslationTriggerMode.Off, TranslationTriggerMode.Both)]
    [InlineData((TranslationTriggerMode)99, TranslationTriggerMode.Both)]
    public void Resume_UsesValidActiveModeOrFallsBackToBoth(
        TranslationTriggerMode lastActive,
        TranslationTriggerMode expected)
    {
        Assert.Equal(expected, TranslationTriggerModes.Resume(lastActive));
    }

    [Theory]
    [InlineData(true, true, TranslationTriggerMode.Both, TranslationTriggerMode.Both)]
    [InlineData(true, false, TranslationTriggerMode.SelectionOnly, TranslationTriggerMode.SelectionOnly)]
    [InlineData(false, true, TranslationTriggerMode.Off, TranslationTriggerMode.Both)]
    [InlineData(false, false, TranslationTriggerMode.Off, TranslationTriggerMode.SelectionOnly)]
    public void MigrateTranslationTriggerMode_LegacyFlags_MapsToExpectedModes(
        bool translationEnabled,
        bool hotKeyEnabled,
        TranslationTriggerMode expectedMode,
        TranslationTriggerMode expectedLastActive)
    {
        var settings = new AppSettings();
        using var document = JsonDocument.Parse(
            $"{{\"TranslationEnabled\":{translationEnabled.ToString().ToLowerInvariant()},\"HotKeyEnabled\":{hotKeyEnabled.ToString().ToLowerInvariant()}}}");

        var changed = ConfigManager.MigrateTranslationTriggerMode(settings, document.RootElement);

        Assert.True(changed);
        Assert.Equal(expectedMode, settings.TranslationTriggerMode);
        Assert.Equal(expectedLastActive, settings.LastActiveTranslationTriggerMode);
    }

    [Fact]
    public void MigrateTranslationTriggerMode_ExistingNewFields_DoesNotReapplyLegacyFlags()
    {
        var settings = new AppSettings
        {
            TranslationTriggerMode = TranslationTriggerMode.HotKeyOnly,
            LastActiveTranslationTriggerMode = TranslationTriggerMode.HotKeyOnly
        };
        using var document = JsonDocument.Parse(
            "{\"TranslationTriggerMode\":\"HotKeyOnly\",\"LastActiveTranslationTriggerMode\":\"HotKeyOnly\",\"TranslationEnabled\":true,\"HotKeyEnabled\":false}");

        var changed = ConfigManager.MigrateTranslationTriggerMode(settings, document.RootElement);

        Assert.False(changed);
        Assert.Equal(TranslationTriggerMode.HotKeyOnly, settings.TranslationTriggerMode);
        Assert.Equal(TranslationTriggerMode.HotKeyOnly, settings.LastActiveTranslationTriggerMode);
    }

    [Fact]
    public void MigrateTranslationTriggerMode_InvalidNewValues_NormalizesToBoth()
    {
        var settings = new AppSettings();
        using var document = JsonDocument.Parse(
            "{\"TranslationTriggerMode\":99,\"LastActiveTranslationTriggerMode\":\"Off\"}");

        var changed = ConfigManager.MigrateTranslationTriggerMode(settings, document.RootElement);

        Assert.True(changed);
        Assert.Equal(TranslationTriggerMode.Both, settings.TranslationTriggerMode);
        Assert.Equal(TranslationTriggerMode.Both, settings.LastActiveTranslationTriggerMode);
    }
}
