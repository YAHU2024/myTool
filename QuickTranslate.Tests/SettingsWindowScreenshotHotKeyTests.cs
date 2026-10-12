using QuickTranslate.Models;
using QuickTranslate.UI;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class SettingsWindowScreenshotHotKeyTests
{
    [Fact]
    public void Window_LoadsDefaultShortcutUsingExistingSettingsStyles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new SettingsWindow(new AppSettings());
                Assert.Equal("Alt+S", window.ScreenshotHotKeyDisplayText.Text);
                Assert.True(window.ScreenshotHotKeyEnabledCheckBox.IsChecked);
                Assert.Same(window.QuickLookupHotKeyEnabledCheckBox.Style,
                    window.ScreenshotHotKeyEnabledCheckBox.Style);
                Assert.Same(window.ChangeQuickLookupHotKeyButton.Style,
                    window.ChangeScreenshotHotKeyButton.Style);
                window.ChangeScreenshotHotKeyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("取消", window.ChangeScreenshotHotKeyButton.Content);
                Assert.Equal(Visibility.Visible, window.ScreenshotHotKeyCaptureHint.Visibility);
                window.ChangeScreenshotHotKeyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("修改", window.ChangeScreenshotHotKeyButton.Content);
                Assert.Equal(Visibility.Collapsed, window.ScreenshotHotKeyCaptureHint.Visibility);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(TranslationTriggerMode.Both)]
    [InlineData(TranslationTriggerMode.HotKeyOnly)]
    public void GetScreenshotHotKeyConflict_DetectsActiveTranslationGesture(
        TranslationTriggerMode translationMode)
    {
        var settings = new AppSettings
        {
            HotKeyVK = 0x53,
            HotKeyRequireAlt = true,
            HotKeyRequireCtrl = false,
            HotKeyRequireShift = false
        };

        Assert.NotNull(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, false, false, false, translationMode));
    }

    [Fact]
    public void GetScreenshotHotKeyConflict_DetectsTranslationGestureWithAdditionalModifiers()
    {
        var settings = new AppSettings
        {
            HotKeyVK = 0x53,
            HotKeyRequireAlt = true,
            HotKeyRequireCtrl = false,
            HotKeyRequireShift = false
        };

        Assert.NotNull(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, true, true, false, TranslationTriggerMode.Both));
    }

    [Fact]
    public void GetScreenshotHotKeyConflict_DetectsActiveLookupGesture()
    {
        var settings = new AppSettings
        {
            QuickLookupHotKeyVK = 0x53,
            QuickLookupHotKeyRequireAlt = false,
            QuickLookupHotKeyRequireCtrl = true,
            QuickLookupHotKeyRequireShift = false
        };

        Assert.NotNull(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, true, false, true, TranslationTriggerMode.Off));
    }

    [Theory]
    [InlineData(TranslationTriggerMode.SelectionOnly)]
    [InlineData(TranslationTriggerMode.Off)]
    public void GetScreenshotHotKeyConflict_IgnoresInactiveTranslationAndLookupGestures(
        TranslationTriggerMode translationMode)
    {
        var settings = new AppSettings
        {
            HotKeyVK = 0x53,
            HotKeyRequireAlt = true,
            HotKeyRequireCtrl = false,
            HotKeyRequireShift = false,
            QuickLookupHotKeyVK = 0x53,
            QuickLookupHotKeyRequireAlt = true,
            QuickLookupHotKeyRequireCtrl = false,
            QuickLookupHotKeyRequireShift = false
        };

        Assert.Null(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, false, false, false, translationMode));
    }

    [Fact]
    public void GetScreenshotHotKeyConflict_AllowsDifferentMainKey()
    {
        var settings = new AppSettings
        {
            HotKeyVK = 0x54,
            HotKeyRequireAlt = true,
            HotKeyRequireCtrl = false,
            HotKeyRequireShift = false,
            QuickLookupHotKeyVK = 0x51,
            QuickLookupHotKeyRequireAlt = true,
            QuickLookupHotKeyRequireCtrl = false,
            QuickLookupHotKeyRequireShift = false
        };

        Assert.Null(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, false, false, true, TranslationTriggerMode.Both));
    }

    [Fact]
    public void GetScreenshotHotKeyConflict_AllowsGestureMissingOtherHooksRequiredModifier()
    {
        var settings = new AppSettings
        {
            HotKeyVK = 0x53,
            HotKeyRequireAlt = true,
            HotKeyRequireCtrl = true,
            HotKeyRequireShift = false,
            QuickLookupHotKeyVK = 0x53,
            QuickLookupHotKeyRequireAlt = true,
            QuickLookupHotKeyRequireCtrl = false,
            QuickLookupHotKeyRequireShift = true
        };

        Assert.Null(SettingsWindow.GetScreenshotHotKeyConflict(
            settings, 0x53, true, false, false, true, TranslationTriggerMode.Both));
    }
}
