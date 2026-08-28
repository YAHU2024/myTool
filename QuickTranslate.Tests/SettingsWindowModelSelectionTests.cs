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
            ModelName = "Qwen/Qwen3-8B",
            ThinkingMode = ThinkingModePreference.Disabled
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
            Assert.Equal(ThinkingModePreference.Disabled, window.ThinkingModeComboBox.SelectedValue);
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

    [SkippableFact]
    public void ThinkingControl_OffersThreeStatesForAdaptedModel()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");

        RunOnSta(new AppSettings
        {
            ApiBaseUrl = "https://api.openai.com/v1",
            ModelName = "gpt-5.4",
            ThinkingMode = ThinkingModePreference.Enabled
        }, window =>
        {
            Assert.True(window.ThinkingModeComboBox.IsEnabled);
            Assert.Equal(3, window.ThinkingModeComboBox.Items.Count);
            Assert.Equal(ThinkingModePreference.Enabled, window.ThinkingModeComboBox.SelectedValue);
            Assert.Contains("已适配", window.ThinkingModeHintText.Text, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public void ThinkingControl_UnknownModelLocksToProviderDefault()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");

        RunOnSta(new AppSettings
        {
            ApiBaseUrl = "https://compatible.example.com/v1",
            ModelName = "default-thinking-model",
            ThinkingMode = ThinkingModePreference.Disabled
        }, window =>
        {
            Assert.False(window.ThinkingModeComboBox.IsEnabled);
            Assert.Single(window.ThinkingModeComboBox.Items);
            Assert.Equal(
                ThinkingModePreference.FollowProviderDefault,
                window.ThinkingModeComboBox.SelectedValue);
            Assert.Contains("由服务端决定", window.ThinkingModeHintText.Text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1", "key-a", "gpt-4o-mini", "https://api.openai.com/v1", "key-a", true)]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1/", "key-a", "gpt-4o-mini", "https://api.openai.com/v1", "key-a", true)]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1", "key-a", "gpt-4o-mini", "https://api.openai.com/V1", "key-a", true)]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1", "key-a", "gpt-4o-mini", "https://api.openai.com/v1", "key-b", false)]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1", "key-a", "gpt-4o", "https://api.openai.com/v1", "key-a", false)]
    [InlineData("gpt-4o-mini", "https://api.openai.com/v1", "key-a", "gpt-4o-mini", "https://other.example/v1", "key-a", false)]
    public void IsCurrentActiveConfig_MatchesOnModelUrlKeyTriple(
        string configModel,
        string configUrl,
        string configKey,
        string settingModel,
        string settingUrl,
        string settingKey,
        bool expected)
    {
        var config = new SavedConfig { ModelName = configModel, ApiBaseUrl = configUrl, ApiKey = configKey };
        var settings = new AppSettings { ModelName = settingModel, ApiBaseUrl = settingUrl, ApiKey = settingKey };

        Assert.Equal(expected, SettingsWindow.IsCurrentActiveConfig(config, settings));
    }

    [Fact]
    public void RebaseCurrentModelAfterDelete_FallsBackToMostRecentRemainingConfig()
    {
        var remaining = new SavedConfig
        {
            ModelName = "glm-4.7-flash",
            ApiBaseUrl = "https://api.zhipuai.cn/v1",
            ApiKey = "zhipu-key",
            ThinkingMode = ThinkingModePreference.Disabled
        };
        var deleted = new SavedConfig
        {
            ModelName = "gpt-4o-mini",
            ApiBaseUrl = "https://api.openai.com/v1",
            ApiKey = "openai-key"
        };
        var settings = new AppSettings
        {
            ModelName = deleted.ModelName,
            ApiBaseUrl = deleted.ApiBaseUrl,
            ApiKey = deleted.ApiKey,
            SavedConfigs = [remaining, deleted]
        };

        settings.SavedConfigs.Remove(deleted);
        SettingsWindow.RebaseCurrentModelAfterDelete(settings);

        Assert.Equal(remaining.ModelName, settings.ModelName);
        Assert.Equal(remaining.ApiBaseUrl, settings.ApiBaseUrl);
        Assert.Equal(remaining.ApiKey, settings.ApiKey);
        Assert.Equal(ThinkingModePreference.Disabled, settings.ThinkingMode);
    }

    [Fact]
    public void RebaseCurrentModelAfterDelete_FallsBackToPresetDefaultWhenNoConfigRemains()
    {
        var deleted = new SavedConfig
        {
            ModelName = "gpt-4o-mini",
            ApiBaseUrl = "https://api.openai.com/v1",
            ApiKey = "openai-key"
        };
        var settings = new AppSettings
        {
            ModelName = deleted.ModelName,
            ApiBaseUrl = deleted.ApiBaseUrl,
            ApiKey = deleted.ApiKey,
            SavedConfigs = [deleted]
        };

        settings.SavedConfigs.Remove(deleted);
        SettingsWindow.RebaseCurrentModelAfterDelete(settings);

        var preset = ProviderPresetCatalog.Default;
        Assert.Equal(preset.ModelName, settings.ModelName);
        Assert.Equal(preset.ApiBaseUrl, settings.ApiBaseUrl);
        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal(ThinkingModePreference.FollowProviderDefault, settings.ThinkingMode);
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
