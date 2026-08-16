using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslate.Models;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class SettingsWindowModelSelectionTests
{
    private static bool IsRunningOnCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [Theory]
    [InlineData("", "Qwen/Qwen3-8B", "长文专用", "Qwen/Qwen3-8B")]
    [InlineData("长文专用", "Qwen/Qwen3-8B", "长文专用", "Qwen/Qwen3-8B")]
    [InlineData("Qwen/Qwen3-8B", "Qwen/Qwen3-8B", "长文专用", "Qwen/Qwen3-8B")]
    [InlineData("custom/model", "Qwen/Qwen3-8B", "长文专用", "custom/model")]
    [InlineData("custom/model", "", "", "custom/model")]
    public void ResolveModelNameForSave_SeparatesDisplayTextFromModelId(
        string editorText,
        string selectedModelName,
        string selectedDisplayName,
        string expected)
    {
        Assert.Equal(
            expected,
            SettingsWindow.ResolveModelNameForSave(
                editorText,
                selectedModelName,
                selectedDisplayName));
    }

    [SkippableFact]
    public void SelectingSavedConfig_ShowsModelIdAndPreservesApiKey()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");

        var savedConfig = new SavedConfig
        {
            Alias = "长文专用",
            DisplayName = "长文专用",
            ApiBaseUrl = "https://api.siliconflow.cn/v1",
            ApiKey = "saved-key",
            ModelName = "Qwen/Qwen3-8B"
        };
        var settings = new AppSettings
        {
            ApiBaseUrl = "https://example.test/v1",
            ApiKey = "current-key",
            ModelName = "current-model",
            SavedConfigs = [savedConfig]
        };

        RunOnSta(settings, window =>
        {
            var savedItem = window.ModelComboBox.Items
                .OfType<ComboBoxItem>()
                .Single(item => ReferenceEquals(item.Tag, savedConfig));

            Assert.Equal("长文专用", savedItem.Content);
            Assert.False(window.ModelComboBox.IsTextSearchEnabled);
            Assert.Equal(
                "Tag.ModelName",
                window.ModelComboBox.GetValue(TextSearch.TextPathProperty));

            window.ModelComboBox.SelectedItem = savedItem;
            PumpDispatcher();

            Assert.Equal("Qwen/Qwen3-8B", window.ModelComboBox.Text);
            Assert.Equal("saved-key", window.ApiKeyPasswordBox.Password);
            Assert.Equal("saved-key", window.ApiKeyVisibleTextBox.Text);
            Assert.Equal("长文专用", window.ModelAliasTextBox.Text);
        });
    }

    [SkippableFact]
    public void AutoDetectionToggle_ControlsFallbackLanguageAvailability()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");

        RunOnSta(new AppSettings { AutoDetectLanguage = false }, window =>
        {
            Assert.False(window.FallbackLanguagePanel.IsEnabled);

            window.AutoDetectLanguageCheckBox.IsChecked = true;
            PumpDispatcher();

            Assert.True(window.FallbackLanguagePanel.IsEnabled);
        });
    }

    private static void RunOnSta(AppSettings settings, Action<SettingsWindow> assertion)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new SettingsWindow(settings);
                assertion(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                PumpDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
