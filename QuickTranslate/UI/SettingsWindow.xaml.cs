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
        private bool _isDirty = false;
        private bool _isApiKeyVisible = false;

        // 原始值快照（用于取消回退）
        private string _origApiBaseUrl = string.Empty;
        private string _origApiKey = string.Empty;
        private string _origModelName = string.Empty;
        private string _origTargetLanguage = string.Empty;
        private bool _origTranslationEnabled = true;
        private bool _origAutoStart = false;

        public SettingsWindow(AppSettings settings, Action<AppSettings>? onSettingsSaved = null)
        {
            _settings = settings;
            _onSettingsSaved = onSettingsSaved;
            InitializeComponent();
            SaveOriginalSnapshot();
            LoadSettings();
            _isInitializing = false;
        }

        /// <summary>
        /// 保存原始值快照（用于取消回退）
        /// </summary>
        private void SaveOriginalSnapshot()
        {
            _origApiBaseUrl = _settings.ApiBaseUrl;
            _origApiKey = _settings.ApiKey;
            _origModelName = _settings.ModelName;
            _origTargetLanguage = _settings.TargetLanguage;
            _origTranslationEnabled = _settings.TranslationEnabled;
            _origAutoStart = _settings.AutoStart;
        }

        private void LoadSettings()
        {
            // API 配置
            ApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
            ApiKeyPasswordBox.Password = _settings.ApiKey;
            ApiKeyVisibleTextBox.Text = _settings.ApiKey;

            // 模型下拉框（按域名分组）
            RefreshModelComboBox();

            // 目标语言
            LanguageComboBox.ItemsSource = _settings.SupportedLanguages;
            LanguageComboBox.SelectedItem = _settings.TargetLanguage;

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

        // ==================== 事件处理 ====================

        /// <summary>
        /// API Key 明文/密文切换
        /// </summary>
        private void EyeButton_Click(object sender, RoutedEventArgs e)
        {
            _isApiKeyVisible = !_isApiKeyVisible;

            if (_isApiKeyVisible)
            {
                // 切换到明文显示
                ApiKeyVisibleTextBox.Text = ApiKeyPasswordBox.Password;
                ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                ApiKeyVisibleTextBox.Visibility = Visibility.Visible;
                EyeButton.Content = "🔒";
                EyeButton.ToolTip = "隐藏 API Key";
            }
            else
            {
                // 切换回密码模式
                ApiKeyPasswordBox.Password = ApiKeyVisibleTextBox.Text;
                ApiKeyVisibleTextBox.Visibility = Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                EyeButton.Content = "👁";
                EyeButton.ToolTip = "显示 API Key";
            }
        }

        /// <summary>
        /// 模型选择变化 - 自动填充 URL 和 Key，显示反馈
        /// </summary>
        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _settings == null) return;

            if (ModelComboBox.SelectedItem is ComboBoxItem cbi && cbi.Tag is SavedConfig config)
            {
                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyPasswordBox.Password = config.ApiKey;
                ApiKeyVisibleTextBox.Text = config.ApiKey;

                // 显示绿色反馈
                var domain = ExtractDomainShortName(config.ApiBaseUrl);
                ModelFeedbackText.Text = $"✓ 已切换至 {config.ModelName}（{domain}）";
                ModelFeedbackText.Visibility = Visibility.Visible;
                _isDirty = true;

                // 3秒后隐藏反馈
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s, args) =>
                {
                    ModelFeedbackText.Visibility = Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            }
        }

        /// <summary>
        /// 语言选择变化
        /// </summary>
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;
        }

        /// <summary>
        /// 已保存配置列表选择 - 自动填充
        /// </summary>
        private void SavedConfigsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (SavedConfigsListBox.SelectedItem is SavedConfig config)
            {
                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyPasswordBox.Password = config.ApiKey;
                ApiKeyVisibleTextBox.Text = config.ApiKey;
                ModelComboBox.Text = config.ModelName;

                // 显示反馈
                ModelFeedbackText.Text = $"✓ 已从列表填充 {config.ModelName}";
                ModelFeedbackText.Visibility = Visibility.Visible;
                _isDirty = true;

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timer.Tick += (s, args) =>
                {
                    ModelFeedbackText.Visibility = Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            }
        }

        /// <summary>
        /// 通用设置变化标记（CheckBox）
        /// </summary>
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;
        }

        /// <summary>
        /// 输入框失去焦点 - 标记为已修改
        /// </summary>
        private void Input_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;
        }

        // ==================== 保存/取消/关闭 ====================

        /// <summary>
        /// 保存按钮 - 落盘
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySettingsToModel();
            ConfigManager.Save(_settings);
            _onSettingsSaved?.Invoke(_settings);
            _isDirty = false;
            Close();
        }

        /// <summary>
        /// 取消按钮 - 回退修改，直接关闭
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _isDirty = false; // 标记无需保存，避免弹窗
            Close();
        }

        /// <summary>
        /// 窗口关闭事件 - 如有未保存修改则弹窗确认
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isDirty)
            {
                var result = MessageBox.Show(
                    "您有未保存的修改，是否保存？",
                    "QuickTranslate 设置",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    ApplySettingsToModel();
                    ConfigManager.Save(_settings);
                    _onSettingsSaved?.Invoke(_settings);
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true; // 取消关闭
                    return;
                }
                // No = 不保存，直接关闭
            }

            base.OnClosing(e);
        }

        /// <summary>
        /// 将界面值应用到配置模型
        /// </summary>
        private void ApplySettingsToModel()
        {
            _settings.ApiBaseUrl = ApiBaseUrlTextBox.Text?.Trim() ?? _settings.ApiBaseUrl;

            // 根据当前显示模式获取 API Key
            _settings.ApiKey = _isApiKeyVisible
                ? (ApiKeyVisibleTextBox.Text ?? _settings.ApiKey)
                : (ApiKeyPasswordBox.Password ?? _settings.ApiKey);

            var model = ModelComboBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(model))
                _settings.ModelName = model;

            if (LanguageComboBox.SelectedItem != null)
                _settings.TargetLanguage = LanguageComboBox.SelectedItem.ToString() ?? _settings.TargetLanguage;

            _settings.TranslationEnabled = TranslationEnabledCheckBox.IsChecked ?? true;

            var autoStart = AutoStartCheckBox.IsChecked ?? false;
            if (autoStart != _origAutoStart)
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
