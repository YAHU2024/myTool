using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly Action<AppSettings>? _onSettingsSaved;
        private bool _isInitializing = true;

        public SettingsWindow(AppSettings settings, Action<AppSettings>? onSettingsSaved = null)
        {
            _settings = settings;
            _onSettingsSaved = onSettingsSaved;
            InitializeComponent();
            LoadSettings();
            _isInitializing = false;
        }

        private void LoadSettings()
        {
            // API 配置
            ApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
            ApiKeyPasswordBox.Password = _settings.ApiKey;

            // 模型下拉框（按域名分组）
            RefreshModelComboBox();

            // 目标语言
            LanguageComboBox.ItemsSource = _settings.SupportedLanguages;
            LanguageComboBox.SelectedItem = _settings.TargetLanguage;
            LanguageComboBox.SelectionChanged += LanguageComboBox_SelectionChanged;

            // 翻译开关
            TranslationEnabledCheckBox.IsChecked = _settings.TranslationEnabled;

            // 开机自启
            AutoStartCheckBox.IsChecked = _settings.AutoStart;
        }

        private void RefreshModelComboBox()
        {
            ModelComboBox.Items.Clear();

            var groups = _settings.SavedConfigs
                .GroupBy(c => ExtractDomainShortName(c.ApiBaseUrl))
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var separator = new ComboBoxItem
                {
                    Content = $"── {group.Key} ──",
                    IsEnabled = false,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99)),
                    FontSize = 11
                };
                ModelComboBox.Items.Add(separator);

                foreach (var config in group)
                {
                    var item = new ComboBoxItem
                    {
                        Content = config.ModelName,
                        Tag = config
                    };
                    ModelComboBox.Items.Add(item);
                }
            }

            ModelComboBox.Text = _settings.ModelName;
        }

        private static string ExtractDomainShortName(string baseUrl)
        {
            try
            {
                var uri = new Uri(baseUrl);
                var host = uri.Host.Replace("api.", "").Replace(".com", "").Replace(".cn", "");
                return host.Length > 12 ? host.Substring(0, 12) : host;
            }
            catch { return "unknown"; }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            // 可以在这里处理语言切换逻辑
        }

        private void SavedConfigsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (SavedConfigsListBox.SelectedItem is SavedConfig config)
            {
                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyPasswordBox.Password = config.ApiKey;
                ModelComboBox.Text = config.ModelName;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 收集设置
            _settings.ApiBaseUrl = ApiBaseUrlTextBox.Text?.Trim() ?? _settings.ApiBaseUrl;
            _settings.ApiKey = ApiKeyPasswordBox.Password ?? _settings.ApiKey;

            var model = ModelComboBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
                _settings.ModelName = model;

            if (LanguageComboBox.SelectedItem != null)
                _settings.TargetLanguage = LanguageComboBox.SelectedItem.ToString() ?? _settings.TargetLanguage;

            _settings.TranslationEnabled = TranslationEnabledCheckBox.IsChecked ?? true;

            var autoStart = AutoStartCheckBox.IsChecked ?? false;
            if (autoStart != _settings.AutoStart)
            {
                _settings.AutoStart = autoStart;
                SetAutoStart(autoStart);
            }

            // 保存到已保存配置列表
            if (!string.IsNullOrWhiteSpace(model))
            {
                _settings.SavedConfigs.RemoveAll(c =>
                    c.ModelName == _settings.ModelName &&
                    c.ApiBaseUrl == _settings.ApiBaseUrl &&
                    c.ApiKey == _settings.ApiKey);

                _settings.SavedConfigs.Insert(0, new SavedConfig
                {
                    DisplayName = _settings.ModelName,
                    ModelName = _settings.ModelName,
                    ApiBaseUrl = _settings.ApiBaseUrl,
                    ApiKey = _settings.ApiKey
                });

                while (_settings.SavedConfigs.Count > 10)
                    _settings.SavedConfigs.RemoveAt(_settings.SavedConfigs.Count - 1);
            }

            // 持久化
            ConfigManager.Save(_settings);

            // 通知 App 更新
            _onSettingsSaved?.Invoke(_settings);

            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 设置或取消开机自启（通过注册表）
        /// </summary>
        private static void SetAutoStart(bool enable)
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                if (enable)
                {
                    key.SetValue("QuickTranslate", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("QuickTranslate", false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置开机自启失败: {ex.Message}");
            }
        }
    }
}
