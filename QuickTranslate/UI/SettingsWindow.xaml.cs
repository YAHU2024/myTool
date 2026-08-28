using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly Action<AppSettings>? _onSettingsSaved;
        private readonly Action<FeedbackMode>? _onFeedbackRequested;
        private readonly Action? _onLogsRequested;
        private bool _isInitializing = true;
        private bool _isDirty = false;
        private bool _isApiKeyVisible = false;
        private readonly bool _origAutoStart;
        private ThinkingModePreference _thinkingModePreference;

        private readonly List<AnalysisPromptProfile> _analysisPromptProfiles = new();
        private string _selectedAnalysisPromptId = AnalysisPromptCatalog.GeneralId;
        private string? _editingAnalysisPromptId;

        // 快捷键录入状态
        private bool _isCapturingHotKey = false;
        private bool _isCapturingQuickLookupHotKey = false;

        public SettingsWindow(
            AppSettings settings,
            Action<AppSettings>? onSettingsSaved = null,
            Action<FeedbackMode>? onFeedbackRequested = null,
            Action? onLogsRequested = null)
        {
            _settings = settings;
            _onSettingsSaved = onSettingsSaved;
            _onFeedbackRequested = onFeedbackRequested;
            _onLogsRequested = onLogsRequested;
            _origAutoStart = settings.AutoStart;
            _thinkingModePreference = ThinkingModePreferences.Normalize(settings.ThinkingMode);
            InitializeComponent();
            LoadSettings();
            _isInitializing = false;
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

            // 翻译触发方式
            LoadTranslationTriggerModeComboBox();
            TranslationTriggerModeComboBox.SelectedValue = TranslationTriggerModes.Normalize(_settings.TranslationTriggerMode);

            // 语言自动检测
            AutoDetectLanguageCheckBox.IsChecked = _settings.AutoDetectLanguage;

            // 备选语言
            FallbackLanguageComboBox.ItemsSource = _settings.SupportedLanguages;
            FallbackLanguageComboBox.SelectedItem = _settings.FallbackLanguage;
            RefreshFallbackLanguageAvailability();

            // 智能内容识别
            SmartContentTypeCheckBox.IsChecked = _settings.SmartContentType;
            RefreshThinkingModeAvailability();

            // 自定义翻译提示词
            CustomTranslationPromptTextBox.Text = _settings.CustomTranslationPrompt;

            _analysisPromptProfiles.Clear();
            _analysisPromptProfiles.AddRange(
                (_settings.AnalysisPromptProfiles ?? new List<AnalysisPromptProfile>())
                    .Select(profile => profile.Clone()));
            _selectedAnalysisPromptId = ResolveAnalysisPromptSelection(_settings.SelectedAnalysisPromptId);
            RefreshAnalysisPromptComboBox();

            // 开机自启
            AutoStartCheckBox.IsChecked = _settings.AutoStart;

            // 自动检查更新
            CheckUpdateOnStartupCheckBox.IsChecked = _settings.CheckForUpdateOnStartup;

            // 快捷键显示
            UpdateHotKeyDisplay();
            UpdateQuickLookupHotKeyDisplay();

            // 快速查词快捷键开关
            QuickLookupHotKeyEnabledCheckBox.IsChecked = _settings.QuickLookupHotKeyEnabled;

            // 浏览器翻译开关
            EnableInBrowserCheckBox.IsChecked = _settings.EnableInBrowser;
            CustomBrowserProcessesTextBox.Text = _settings.CustomBrowserProcesses;

            TerminalCopyModeComboBox.ItemsSource = new[]
            {
                new { Value = "Smart", Name = "智能（推荐）" },
                new { Value = "Compatible", Name = "兼容（统一使用 Ctrl+Shift+C）" },
                new { Value = "Disabled", Name = "禁用终端取词" }
            };
            TerminalCopyModeComboBox.DisplayMemberPath = "Name";
            TerminalCopyModeComboBox.SelectedValuePath = "Value";
            TerminalCopyModeComboBox.SelectedValue = _settings.TerminalCopyMode;
            TerminalCopyMappingsTextBox.Text = _settings.TerminalCopyMappings;

            LogLevelComboBox.ItemsSource = new[] { "Debug", "Info", "Warn", "Error", "Fatal" };
            LogLevelComboBox.SelectedItem = Logger.ParseLevel(_settings.LogLevel).ToString();
            LogRetentionDaysTextBox.Text = _settings.LogRetentionDays.ToString();
            LogMaxTotalMegabytesTextBox.Text = Math.Max(1, _settings.LogMaxTotalBytes / (1024 * 1024)).ToString();
            CrashFeedbackPromptCheckBox.IsChecked = _settings.CrashFeedbackPromptEnabled;

            TtsEnabledCheckBox.IsChecked = _settings.TtsEnabled;
            TtsVoiceComboBox.ItemsSource = new[]
            {
                new { Value = "", Name = "自动（按文本语言）" },
                new { Value = "zh-CN-XiaoxiaoNeural", Name = "晓晓（中文）" },
                new { Value = "zh-CN-YunxiNeural", Name = "云希（中文）" },
                new { Value = "en-US-JennyNeural", Name = "Jenny（英文）" },
                new { Value = "en-US-GuyNeural", Name = "Guy（英文）" }
            };
            TtsVoiceComboBox.DisplayMemberPath = "Name";
            TtsVoiceComboBox.SelectedValuePath = "Value";
            var voice = _settings.TtsVoice ?? string.Empty;
            if (TtsVoiceComboBox.Items.Cast<object>().All(item =>
                    item.GetType().GetProperty("Value")?.GetValue(item)?.ToString() != voice))
                voice = string.Empty;
            TtsVoiceComboBox.SelectedValue = voice;

            TtsRateComboBox.ItemsSource = new[]
            {
                new { Value = 0.9, Name = "0.9x" },
                new { Value = 1.0, Name = "1.0x" },
                new { Value = 1.1, Name = "1.1x" }
            };
            TtsRateComboBox.DisplayMemberPath = "Name";
            TtsRateComboBox.SelectedValuePath = "Value";
            var rate = _settings.TtsRate;
            if (rate is not (0.9 or 1.0 or 1.1))
                rate = 1.0;
            TtsRateComboBox.SelectedValue = rate;
        }

        /// <summary>
        /// 更新快捷键显示文本
        /// </summary>
        private void UpdateHotKeyDisplay()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (_settings.HotKeyRequireCtrl) parts.Add("Ctrl");
            if (_settings.HotKeyRequireAlt) parts.Add("Alt");
            if (_settings.HotKeyRequireShift) parts.Add("Shift");
            parts.Add(GetKeyName(_settings.HotKeyVK));
            HotKeyDisplayText.Text = string.Join("+", parts);
        }

        /// <summary>
        /// 获取按键名称
        /// </summary>
        private static string GetKeyName(byte vk)
        {
            return vk switch
            {
                0x51 => "Q",
                0x57 => "W",
                0x45 => "E",
                0x52 => "R",
                0x54 => "T",
                0x59 => "Y",
                0x55 => "U",
                0x49 => "I",
                0x4F => "O",
                0x50 => "P",
                0x41 => "A",
                0x53 => "S",
                0x44 => "D",
                0x46 => "F",
                0x47 => "G",
                0x48 => "H",
                0x4A => "J",
                0x4B => "K",
                0x4C => "L",
                0x5A => "Z",
                0x58 => "X",
                0x43 => "C",
                0x56 => "V",
                0x42 => "B",
                0x4E => "N",
                0x4D => "M",
                0x30 => "0",
                0x31 => "1",
                0x32 => "2",
                0x33 => "3",
                0x34 => "4",
                0x35 => "5",
                0x36 => "6",
                0x37 => "7",
                0x38 => "8",
                0x39 => "9",
                0x20 => "Space",
                _ => $"VK_{vk:X2}"
            };
        }

        private void RefreshModelComboBox()
        {
            ModelComboBox.Items.Clear();

            AddModelGroupHeader("供应商预置");
            foreach (var preset in ProviderPresetCatalog.All)
            {
                ModelComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{preset.ModelName} · {preset.DisplayName}",
                    Tag = preset
                });
            }

            var groups = _settings.SavedConfigs
                .GroupBy(c => ExtractDomainShortName(c.ApiBaseUrl))
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                AddModelGroupHeader(group.Key);

                foreach (var config in group)
                {
                    var profile = ModelProfileCatalog.Create(config);
                    var item = new ComboBoxItem
                    {
                        Content = profile.DisplayName,
                        Tag = config
                    };
                    ModelComboBox.Items.Add(item);
                }
            }

            var current = _settings.SavedConfigs.FirstOrDefault(config =>
                IsCurrentActiveConfig(config, _settings));
            ModelAliasTextBox.Text = current is null
                ? string.Empty
                : ModelProfileCatalog.ResolveLegacyAlias(current);

            // 高亮当前生效的已保存配置；预置与自由输入不定位（避免误触发预置分支清空 API Key）
            ModelComboBox.SelectedItem = current is null
                ? null
                : ModelComboBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => ReferenceEquals(item.Tag, current));
            ModelComboBox.Text = _settings.ModelName;
        }

        private void AddModelGroupHeader(string title)
        {
            ModelComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"── {title} ──",
                IsEnabled = false,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99)),
                FontSize = 11
            });
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
                EyeButton.Content = "\uE72E";
                EyeButton.ToolTip = "隐藏 API 密钥";
            }
            else
            {
                // 切换回密码模式
                ApiKeyPasswordBox.Password = ApiKeyVisibleTextBox.Text;
                ApiKeyVisibleTextBox.Visibility = Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                EyeButton.Content = "\uE890";
                EyeButton.ToolTip = "显示 API 密钥";
            }
        }

        /// <summary>
        /// 模型选择变化 - 自动填充 URL 和 Key，同步删除按钮状态
        /// </summary>
        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 同步删除按钮状态：仅当选中已保存的配置时启用
            DeleteConfigButton.IsEnabled = ModelComboBox.SelectedItem is ComboBoxItem cbi && cbi.Tag is SavedConfig;

            if (_isInitializing || _settings == null) return;

            if (ModelComboBox.SelectedItem is ComboBoxItem cbi2 && cbi2.Tag is ProviderPreset preset)
            {
                ApiBaseUrlTextBox.Text = preset.ApiBaseUrl;
                ApiKeyPasswordBox.Password = string.Empty;
                ApiKeyVisibleTextBox.Text = string.Empty;
                ModelAliasTextBox.Text = string.Empty;
                _thinkingModePreference = ThinkingModePreference.FollowProviderDefault;
                ShowModelFeedback($"已选择 {preset.DisplayName}，请填写 API Key", autoHide: false);
                _isDirty = true;
            }
            else if (ModelComboBox.SelectedItem is ComboBoxItem cbi3 && cbi3.Tag is SavedConfig config)
            {
                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyPasswordBox.Password = config.ApiKey;
                ApiKeyVisibleTextBox.Text = config.ApiKey;
                ModelAliasTextBox.Text = ModelProfileCatalog.ResolveLegacyAlias(config);
                _thinkingModePreference = ThinkingModePreferences.Normalize(config.ThinkingMode);

                var domain = ExtractDomainShortName(config.ApiBaseUrl);
                ShowModelFeedback($"已切换到 {config.ModelName}（{domain}）", autoHide: true);
                _isDirty = true;
            }

            RefreshThinkingModeAvailability();
        }

        public void ShowConfigurationNotice(string message, bool isWarning)
        {
            ModelFeedbackText.Text = message;
            ModelFeedbackText.Foreground = new System.Windows.Media.SolidColorBrush(
                isWarning
                    ? System.Windows.Media.Color.FromRgb(0xF2, 0xC6, 0x6D)
                    : System.Windows.Media.Color.FromRgb(0x6D, 0xD6, 0xA5));
            ModelFeedbackText.Visibility = Visibility.Visible;
            ApiKeyPasswordBox.Focus();
        }

        private void ShowModelFeedback(string message, bool autoHide)
        {
            ModelFeedbackText.Text = message;
            ModelFeedbackText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x6D, 0xD6, 0xA5));
            ModelFeedbackText.Visibility = Visibility.Visible;

            if (autoHide)
            {
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

        private void RefreshThinkingModeAvailability()
        {
            var capabilities = ProviderRequestPolicy.ResolveCapabilities(
                ApiBaseUrlTextBox.Text?.Trim() ?? string.Empty,
                ResolveCurrentThinkingModelName());
            IReadOnlyList<ThinkingModeChoice> choices;
            string hint;
            switch (capabilities.ThinkingControlAvailability)
            {
                case ThinkingControlAvailability.Controllable:
                    choices = BuildThinkingModeChoices(capabilities);
                    _thinkingModePreference = ThinkingModePreferences.Normalize(_thinkingModePreference);
                    if (!choices.Any(choice => choice.Value == _thinkingModePreference))
                        _thinkingModePreference = ThinkingModePreference.FollowProviderDefault;
                    ThinkingModeComboBox.IsEnabled = true;
                    hint = "当前模型已适配，可明确开启、关闭或交由服务端决定。";
                    break;
                case ThinkingControlAvailability.Unsupported:
                    choices = [new(ThinkingModePreference.FollowProviderDefault, "不支持思考")];
                    _thinkingModePreference = ThinkingModePreference.FollowProviderDefault;
                    ThinkingModeComboBox.IsEnabled = false;
                    hint = "当前模型已确认不支持思考模式。";
                    break;
                default:
                    choices = [new(ThinkingModePreference.FollowProviderDefault, "跟随模型默认")];
                    _thinkingModePreference = ThinkingModePreference.FollowProviderDefault;
                    ThinkingModeComboBox.IsEnabled = false;
                    hint = "尚未适配该模型的思考参数；实际行为由服务端决定，返回的思考内容仍会正常展示。";
                    break;
            }

            ThinkingModeComboBox.ItemsSource = choices;
            ThinkingModeComboBox.SelectedValue = _thinkingModePreference;
            ThinkingModeComboBox.ToolTip = hint;
            ThinkingModeHintText.Text = hint;
            AutomationProperties.SetHelpText(ThinkingModeComboBox, hint);
        }

        private string ResolveCurrentThinkingModelName() =>
            ModelComboBox.SelectedItem is ComboBoxItem item
                ? item.Tag switch
                {
                    ProviderPreset preset => preset.ModelName,
                    SavedConfig config => config.ModelName,
                    _ => ModelComboBox.Text?.Trim() ?? string.Empty
                }
                : ModelComboBox.Text?.Trim() ?? string.Empty;

        private static IReadOnlyList<ThinkingModeChoice> BuildThinkingModeChoices(
            ProviderModelCapabilities capabilities)
        {
            var choices = new List<ThinkingModeChoice>
            {
                new(ThinkingModePreference.FollowProviderDefault, "跟随模型默认")
            };
            if (capabilities.CanEnableThinking)
                choices.Add(new(ThinkingModePreference.Enabled, "开启思考"));
            if (capabilities.CanDisableThinking)
                choices.Add(new(ThinkingModePreference.Disabled, "关闭思考"));
            return choices;
        }

        private void ThinkingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThinkingModeComboBox.SelectedValue is ThinkingModePreference preference)
                _thinkingModePreference = preference;
            if (!_isInitializing)
                _isDirty = true;
        }

        /// <summary>
        /// 语言选择变化
        /// </summary>
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;

            // 智能默认：目标语言变化时自动推荐备选语言
            var target = LanguageComboBox.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(target))
            {
                var recommended = AppSettings.GetRecommendedFallback(target);
                if (FallbackLanguageComboBox.SelectedItem?.ToString() != recommended)
                {
                    FallbackLanguageComboBox.SelectedItem = recommended;
                }
            }
        }

        private void FallbackLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;
        }

        private void TerminalCopyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            _isDirty = true;
        }

        private void AnalysisPromptComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            SaveAnalysisPromptEditor();
            if (AnalysisPromptComboBox.SelectedValue is string selectedId)
                _selectedAnalysisPromptId = selectedId;
            LoadAnalysisPromptEditor();
            _isDirty = true;
        }

        private void NewAnalysisPromptButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAnalysisPromptEditor();
            var profile = new AnalysisPromptProfile
            {
                Id = $"custom:{Guid.NewGuid():N}",
                Name = GetUniqueAnalysisPromptName("自定义解析"),
                Prompt = AnalysisPromptCatalog.GetBuiltInOrGeneral(AnalysisPromptCatalog.GeneralId).PromptTemplate
            };
            _analysisPromptProfiles.Add(profile);
            _selectedAnalysisPromptId = profile.Id;
            RefreshAnalysisPromptComboBox();
            _isDirty = true;
        }

        private void CopyAnalysisPromptButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAnalysisPromptEditor();
            var selectedChoice = AnalysisPromptComboBox.SelectedItem as AnalysisPromptChoice;
            if (selectedChoice == null)
                return;

            var sourcePrompt = selectedChoice.IsBuiltIn
                ? AnalysisPromptCatalog.GetBuiltInOrGeneral(selectedChoice.Id).PromptTemplate
                : _analysisPromptProfiles.First(profile => profile.Id == selectedChoice.Id).Prompt;
            var profile = new AnalysisPromptProfile
            {
                Id = $"custom:{Guid.NewGuid():N}",
                Name = GetUniqueAnalysisPromptName($"{selectedChoice.Name} 副本"),
                Prompt = sourcePrompt
            };
            _analysisPromptProfiles.Add(profile);
            _selectedAnalysisPromptId = profile.Id;
            RefreshAnalysisPromptComboBox();
            _isDirty = true;
        }

        private void DeleteAnalysisPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectedAnalysisPromptId.StartsWith("custom:", StringComparison.Ordinal))
                return;

            var profile = _analysisPromptProfiles.FirstOrDefault(item => item.Id == _selectedAnalysisPromptId);
            if (profile == null)
                return;
            var result = MessageBox.Show(
                $"确定要删除解析方案\u201c{profile.Name}\u201d吗？",
                "删除解析方案",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            _analysisPromptProfiles.Remove(profile);
            _selectedAnalysisPromptId = AnalysisPromptCatalog.GeneralId;
            RefreshAnalysisPromptComboBox();
            _isDirty = true;
        }

        private void AnalysisPromptEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing || _editingAnalysisPromptId == null)
                return;
            _isDirty = true;
        }

        private void AnalysisPromptEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveAnalysisPromptEditor();
            RefreshAnalysisPromptComboBox();
        }

        /// <summary>
        /// 删除选中的已保存配置（从模型下拉框中移除）
        /// </summary>
        private void DeleteConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (ModelComboBox.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not SavedConfig config)
                return;

            var modelName = config.ModelName;

            // 二次确认
            var confirmResult = MessageBox.Show(
                $"确定要删除模型配置 \"{modelName}\" 吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmResult != MessageBoxResult.Yes)
                return;

            var isCurrent = IsCurrentActiveConfig(config, _settings);
            _settings.SavedConfigs.Remove(config);
            DeleteConfigButton.IsEnabled = false;
            _isDirty = true;

            // 删除当前生效配置时，将当前模型重定位到剩余配置或预置默认，
            // 避免下拉框残留已删除名称，并防止保存时被重新写回（复活）。
            if (isCurrent)
                RebaseCurrentModelAfterDelete(_settings);

            // 刷新模型下拉框（内部会高亮剩余配置并回填 URL/Key/Alias）
            RefreshModelComboBox();

            // 回退到预置默认时下拉框无选中项，手动回填基础配置
            if (isCurrent && _settings.SavedConfigs.Count == 0)
            {
                var preset = ProviderPresetCatalog.Default;
                ApiBaseUrlTextBox.Text = preset.ApiBaseUrl;
                ApiKeyPasswordBox.Password = string.Empty;
                ApiKeyVisibleTextBox.Text = string.Empty;
                ModelAliasTextBox.Text = string.Empty;
                RefreshThinkingModeAvailability();
            }

            // 显示反馈
            ModelFeedbackText.Text = $"已删除 {modelName}";
            ModelFeedbackText.Visibility = Visibility.Visible;
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

        /// <summary>
        /// 判断配置是否为当前生效模型（模型名、Base URL、API Key 三要素全等；
        /// Base URL 忽略末尾斜杠与大小写）。
        /// </summary>
        internal static bool IsCurrentActiveConfig(SavedConfig config, AppSettings settings) =>
            string.Equals(config.ModelName, settings.ModelName, StringComparison.Ordinal) &&
            string.Equals(
                (config.ApiBaseUrl ?? string.Empty).TrimEnd('/'),
                (settings.ApiBaseUrl ?? string.Empty).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(config.ApiKey, settings.ApiKey, StringComparison.Ordinal);

        /// <summary>
        /// 删除当前生效配置后，将当前模型重定位到剩余最近使用配置；
        /// 无剩余时回退供应商预置默认（API Key 清空、思考模式跟随默认）。
        /// 调用前提：被删配置已从 SavedConfigs 移除。
        /// </summary>
        internal static void RebaseCurrentModelAfterDelete(AppSettings settings)
        {
            var fallback = settings.SavedConfigs.FirstOrDefault();
            if (fallback != null)
            {
                settings.ModelName = fallback.ModelName ?? string.Empty;
                settings.ApiBaseUrl = fallback.ApiBaseUrl ?? string.Empty;
                settings.ApiKey = fallback.ApiKey ?? string.Empty;
                settings.ThinkingMode = ThinkingModePreferences.Normalize(fallback.ThinkingMode);
                return;
            }

            var preset = ProviderPresetCatalog.Default;
            settings.ModelName = preset.ModelName;
            settings.ApiBaseUrl = preset.ApiBaseUrl;
            settings.ApiKey = string.Empty;
            settings.ThinkingMode = ThinkingModePreference.FollowProviderDefault;
        }

        /// <summary>
        /// 通用设置变化标记（CheckBox）
        /// </summary>
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (ReferenceEquals(sender, AutoDetectLanguageCheckBox))
                RefreshFallbackLanguageAvailability();
            _isDirty = true;
        }

        private void RefreshFallbackLanguageAvailability()
        {
            if (FallbackLanguagePanel is not null)
                FallbackLanguagePanel.IsEnabled = AutoDetectLanguageCheckBox.IsChecked == true;
        }

        /// <summary>
        /// 快速查词快捷键开关变更
        /// </summary>
        private void QuickLookupHotKeyEnabled_Changed(object sender, RoutedEventArgs e)
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
            if (ReferenceEquals(sender, ApiBaseUrlTextBox) || ReferenceEquals(sender, ModelComboBox))
                RefreshThinkingModeAvailability();
        }

        private void ReportProblemButton_Click(object sender, RoutedEventArgs e) =>
            _onFeedbackRequested?.Invoke(FeedbackMode.Problem);

        private void FeatureRequestButton_Click(object sender, RoutedEventArgs e) =>
            _onFeedbackRequested?.Invoke(FeedbackMode.FeatureRequest);

        private void ViewLogsButton_Click(object sender, RoutedEventArgs e) =>
            _onLogsRequested?.Invoke();

        // ==================== 保存/取消/关闭 ====================

        /// <summary>
        /// 保存按钮 - 落盘
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAnalysisPromptEditor();
            if (!ValidateAnalysisPromptProfiles())
                return;
            if (!ValidateApiBaseUrl())
                return;
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
                    "设置尚未保存，是否保存后关闭？",
                    "QuickTranslate 设置",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    SaveAnalysisPromptEditor();
                    if (!ValidateAnalysisPromptProfiles())
                    {
                        e.Cancel = true;
                        return;
                    }
                    if (!ValidateApiBaseUrl())
                    {
                        e.Cancel = true;
                        return;
                    }
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
            _settings.ApiBaseUrl = ApiEndpointValidator.ValidateAndNormalize(
                ApiBaseUrlTextBox.Text?.Trim() ?? _settings.ApiBaseUrl);

            // 根据当前显示模式获取 API Key
            _settings.ApiKey = _isApiKeyVisible
                ? (ApiKeyVisibleTextBox.Text ?? _settings.ApiKey)
                : (ApiKeyPasswordBox.Password ?? _settings.ApiKey);

            var selectedItem = ModelComboBox.SelectedItem as ComboBoxItem;
            var selectedModel = selectedItem?.Tag switch
            {
                ProviderPreset preset => preset.ModelName,
                SavedConfig config => config.ModelName,
                _ => string.Empty
            };
            var model = ResolveModelNameForSave(
                ModelComboBox.Text,
                selectedModel,
                selectedItem?.Content?.ToString());
            if (!string.IsNullOrWhiteSpace(model))
                _settings.ModelName = model;

            if (LanguageComboBox.SelectedItem != null)
                _settings.TargetLanguage = LanguageComboBox.SelectedItem.ToString() ?? _settings.TargetLanguage;

            _settings.TranslationTriggerMode = TranslationTriggerModeComboBox.SelectedValue is TranslationTriggerMode selectedMode
                ? TranslationTriggerModes.Normalize(selectedMode)
                : TranslationTriggerMode.Both;
            _settings.LastActiveTranslationTriggerMode = TranslationTriggerModes.RememberActiveIfNeeded(
                _settings.TranslationTriggerMode,
                _settings.LastActiveTranslationTriggerMode);

            _settings.AutoDetectLanguage = AutoDetectLanguageCheckBox.IsChecked ?? true;

            _settings.SmartContentType = SmartContentTypeCheckBox.IsChecked ?? false;
            _settings.ThinkingMode = _thinkingModePreference;

            if (FallbackLanguageComboBox.SelectedItem != null)
                _settings.FallbackLanguage = FallbackLanguageComboBox.SelectedItem.ToString() ?? _settings.FallbackLanguage;

            _settings.CustomTranslationPrompt = CustomTranslationPromptTextBox.Text?.Trim() ?? string.Empty;
            SaveAnalysisPromptEditor();
            _settings.SelectedAnalysisPromptId = ResolveAnalysisPromptSelection(_selectedAnalysisPromptId);
            _settings.AnalysisPromptProfiles = _analysisPromptProfiles
                .Select(profile => profile.Clone())
                .ToList();
            _settings.CustomAnalysisPrompt = string.Empty;
            _settings.AnalysisPreset = _settings.SelectedAnalysisPromptId.StartsWith("builtin:", StringComparison.Ordinal)
                ? _settings.SelectedAnalysisPromptId["builtin:".Length..]
                : "general";

            _settings.EnableInBrowser = EnableInBrowserCheckBox.IsChecked ?? true;
            _settings.CustomBrowserProcesses = CustomBrowserProcessesTextBox.Text?.Trim() ?? string.Empty;
            if (TerminalCopyModeComboBox.SelectedValue is string terminalMode)
                _settings.TerminalCopyMode = terminalMode;
            _settings.TerminalCopyMappings = TerminalCopyMappingsTextBox.Text?.Trim() ?? string.Empty;

            if (LogLevelComboBox.SelectedItem is string logLevel)
                _settings.LogLevel = logLevel;
            if (!int.TryParse(LogRetentionDaysTextBox.Text, out var retentionDays))
                retentionDays = 7;
            if (!long.TryParse(LogMaxTotalMegabytesTextBox.Text, out var maxMegabytes))
                maxMegabytes = 50;
            _settings.LogRetentionDays = Math.Clamp(retentionDays, 1, 3650);
            _settings.LogMaxTotalBytes = Math.Clamp(maxMegabytes * 1024 * 1024, 1 * 1024 * 1024, 1024L * 1024 * 1024);
            _settings.CrashFeedbackPromptEnabled = CrashFeedbackPromptCheckBox.IsChecked ?? true;

            _settings.TtsEnabled = TtsEnabledCheckBox.IsChecked ?? true;
            _settings.TtsVoice = TtsVoiceComboBox.SelectedValue as string ?? string.Empty;
            if (TtsRateComboBox.SelectedValue is double ttsRate)
                _settings.TtsRate = ttsRate;
            else if (TtsRateComboBox.SelectedValue is not null &&
                     double.TryParse(TtsRateComboBox.SelectedValue.ToString(), out var parsedRate))
                _settings.TtsRate = parsedRate;
            if (_settings.TtsMaxChars <= 0)
                _settings.TtsMaxChars = 2000;

            var autoStart = AutoStartCheckBox.IsChecked ?? false;
            if (autoStart != _origAutoStart)
            {
                _settings.AutoStart = autoStart;
                SetAutoStart(autoStart);
            }

            _settings.CheckForUpdateOnStartup = CheckUpdateOnStartupCheckBox.IsChecked ?? true;

            _settings.QuickLookupHotKeyEnabled = QuickLookupHotKeyEnabledCheckBox.IsChecked ?? false;

            // 保存到已保存配置列表
            if (!string.IsNullOrWhiteSpace(model))
            {
                var alias = ModelProfileCatalog.NormalizeAlias(ModelAliasTextBox.Text);
                var existing = _settings.SavedConfigs.FirstOrDefault(c =>
                    c.ModelName == _settings.ModelName &&
                    c.ApiBaseUrl == _settings.ApiBaseUrl &&
                    c.ApiKey == _settings.ApiKey);

                if (existing is not null)
                    _settings.SavedConfigs.Remove(existing);

                var saved = existing ?? new SavedConfig
                {
                    ModelName = _settings.ModelName,
                    ApiBaseUrl = _settings.ApiBaseUrl,
                    ApiKey = _settings.ApiKey
                };
                saved.Alias = alias;
                saved.DisplayName = string.IsNullOrWhiteSpace(alias) ? saved.ModelName : alias;
                saved.ThinkingMode = _settings.ThinkingMode;
                _settings.SavedConfigs.Insert(0, saved);

                while (_settings.SavedConfigs.Count > 10)
                    _settings.SavedConfigs.RemoveAt(_settings.SavedConfigs.Count - 1);
            }

        }

        internal static string ResolveModelNameForSave(
            string? editorText,
            string? selectedModelName,
            string? selectedDisplayName)
        {
            var normalizedEditorText = editorText?.Trim() ?? string.Empty;
            var normalizedSelectedModel = selectedModelName?.Trim() ?? string.Empty;
            var normalizedDisplayName = selectedDisplayName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedSelectedModel))
                return normalizedEditorText;

            if (string.IsNullOrWhiteSpace(normalizedEditorText) ||
                string.Equals(normalizedEditorText, normalizedSelectedModel, StringComparison.Ordinal) ||
                string.Equals(normalizedEditorText, normalizedDisplayName, StringComparison.Ordinal))
            {
                return normalizedSelectedModel;
            }

            return normalizedEditorText;
        }

        private sealed record ThinkingModeChoice(ThinkingModePreference Value, string Label);

        private void LoadTranslationTriggerModeComboBox()
        {
            TranslationTriggerModeComboBox.ItemsSource = new[]
            {
                new TranslationTriggerModeChoice(TranslationTriggerMode.Both),
                new TranslationTriggerModeChoice(TranslationTriggerMode.SelectionOnly),
                new TranslationTriggerModeChoice(TranslationTriggerMode.HotKeyOnly),
                new TranslationTriggerModeChoice(TranslationTriggerMode.Off)
            };
            TranslationTriggerModeComboBox.DisplayMemberPath = nameof(TranslationTriggerModeChoice.Name);
            TranslationTriggerModeComboBox.SelectedValuePath = nameof(TranslationTriggerModeChoice.Mode);
        }

        private sealed class TranslationTriggerModeChoice
        {
            public TranslationTriggerModeChoice(TranslationTriggerMode mode)
            {
                Mode = mode;
                Name = TranslationTriggerModes.GetDisplayName(mode);
            }

            public TranslationTriggerMode Mode { get; }

            public string Name { get; }
        }

        private bool ValidateAnalysisPromptProfiles()
        {
            var invalidProfile = _analysisPromptProfiles.FirstOrDefault(profile =>
                string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Prompt));
            if (invalidProfile != null)
            {
                MessageBox.Show(
                    "请填写自定义解析方案的名称和提示词。",
                    "解析方案不完整",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            var duplicateName = _analysisPromptProfiles
                .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateName != null)
            {
                MessageBox.Show(
                    $"解析方案名称\u201c{duplicateName.Key}\u201d已存在，请使用其他名称。",
                    "解析方案名称重复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates that the API Base URL does not send credentials over
        /// plaintext HTTP to a remote host. Loopback HTTP is allowed for
        /// local development.
        /// </summary>
        private bool ValidateApiBaseUrl()
        {
            var url = ApiBaseUrlTextBox.Text?.Trim() ?? string.Empty;
            var error = ApiEndpointValidator.Validate(url);
            if (error == null)
                return true;

            MessageBox.Show(
                error,
                "API 地址无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private void RefreshAnalysisPromptComboBox()
        {
            var choices = AnalysisPromptCatalog.BuiltIns
                .Select(prompt => new AnalysisPromptChoice(prompt.Id, prompt.Name, true))
                .Concat(_analysisPromptProfiles.Select(profile =>
                    new AnalysisPromptChoice(profile.Id, profile.Name, false)))
                .ToArray();
            AnalysisPromptComboBox.ItemsSource = choices;
            AnalysisPromptComboBox.DisplayMemberPath = nameof(AnalysisPromptChoice.Name);
            AnalysisPromptComboBox.SelectedValuePath = nameof(AnalysisPromptChoice.Id);
            AnalysisPromptComboBox.SelectedValue = ResolveAnalysisPromptSelection(_selectedAnalysisPromptId);
            LoadAnalysisPromptEditor();
        }

        private void LoadAnalysisPromptEditor()
        {
            var profile = _analysisPromptProfiles.FirstOrDefault(item => item.Id == _selectedAnalysisPromptId);
            _editingAnalysisPromptId = profile?.Id;
            AnalysisPromptEditorPanel.Visibility = profile == null ? Visibility.Collapsed : Visibility.Visible;
            DeleteAnalysisPromptButton.IsEnabled = profile != null;
            if (profile == null)
            {
                AnalysisPromptNameTextBox.Text = string.Empty;
                AnalysisPromptTextBox.Text = string.Empty;
                return;
            }

            AnalysisPromptNameTextBox.Text = profile.Name;
            AnalysisPromptTextBox.Text = profile.Prompt;
        }

        private void SaveAnalysisPromptEditor()
        {
            if (_editingAnalysisPromptId == null)
                return;
            var profile = _analysisPromptProfiles.FirstOrDefault(item => item.Id == _editingAnalysisPromptId);
            if (profile == null)
                return;
            profile.Name = string.IsNullOrWhiteSpace(AnalysisPromptNameTextBox.Text)
                ? "未命名解析"
                : AnalysisPromptNameTextBox.Text.Trim();
            profile.Prompt = AnalysisPromptTextBox.Text?.Trim() ?? string.Empty;
        }

        private string ResolveAnalysisPromptSelection(string? selectedId)
        {
            if (AnalysisPromptCatalog.IsBuiltIn(selectedId))
                return selectedId!;
            if (selectedId?.StartsWith("custom:", StringComparison.Ordinal) == true &&
                _analysisPromptProfiles.Any(profile => profile.Id == selectedId))
                return selectedId;
            return AnalysisPromptCatalog.GeneralId;
        }

        private string GetUniqueAnalysisPromptName(string baseName)
        {
            var names = new HashSet<string>(
                _analysisPromptProfiles.Select(profile => profile.Name),
                StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName))
                return baseName;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{baseName} {suffix}";
                if (!names.Contains(candidate))
                    return candidate;
            }
        }

        // ==================== 快捷键录入 ====================

        /// <summary>
        /// 修改快捷键按钮点击
        /// </summary>
        private void ChangeHotKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingQuickLookupHotKey)
                return; // 快速查词快捷键正在录入，忽略

            if (_isCapturingHotKey)
            {
                // 取消录入
                StopHotKeyCapture();
                return;
            }

            // 开始录入
            _isCapturingHotKey = true;
            ChangeHotKeyButton.Content = "取消";
            HotKeyCaptureHint.Visibility = Visibility.Visible;
            HotKeyDisplayText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00)); // 橙色

            // 捕获键盘事件
            this.PreviewKeyDown += HotKeyCapture_KeyDown;
        }

        /// <summary>
        /// 快捷键录入键盘事件
        /// </summary>
        private void HotKeyCapture_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_isCapturingHotKey) return;

            // 忽略单独的修饰键
            if (e.Key == System.Windows.Input.Key.LeftCtrl || e.Key == System.Windows.Input.Key.RightCtrl ||
                e.Key == System.Windows.Input.Key.LeftAlt || e.Key == System.Windows.Input.Key.RightAlt ||
                e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift)
            {
                return;
            }

            // 获取按键组合
            var vk = (byte)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
            var requireAlt = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
            var requireCtrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            var requireShift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);

            // 至少需要一个修饰键
            if (!requireAlt && !requireCtrl && !requireShift)
            {
                MessageBox.Show("快捷键必须包含 Ctrl、Alt 或 Shift 中的至少一个修饰键", "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 应用新快捷键
            _settings.HotKeyVK = vk;
            _settings.HotKeyRequireAlt = requireAlt;
            _settings.HotKeyRequireCtrl = requireCtrl;
            _settings.HotKeyRequireShift = requireShift;

            UpdateHotKeyDisplay();
            StopHotKeyCapture();
            _isDirty = true;

            e.Handled = true;
        }

        /// <summary>
        /// 停止快捷键录入
        /// </summary>
        private void StopHotKeyCapture()
        {
            _isCapturingHotKey = false;
            ChangeHotKeyButton.Content = "修改";
            HotKeyCaptureHint.Visibility = Visibility.Collapsed;
            HotKeyDisplayText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)); // 白色

            this.PreviewKeyDown -= HotKeyCapture_KeyDown;
        }

        // ==================== 快速查词快捷键录入 ====================

        /// <summary>
        /// 更新快速查词快捷键显示文本
        /// </summary>
        private void UpdateQuickLookupHotKeyDisplay()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (_settings.QuickLookupHotKeyRequireCtrl) parts.Add("Ctrl");
            if (_settings.QuickLookupHotKeyRequireAlt) parts.Add("Alt");
            if (_settings.QuickLookupHotKeyRequireShift) parts.Add("Shift");
            parts.Add(GetKeyName(_settings.QuickLookupHotKeyVK));
            QuickLookupHotKeyDisplayText.Text = string.Join("+", parts);
        }

        /// <summary>
        /// 快速查词快捷键修改按钮
        /// </summary>
        private void ChangeQuickLookupHotKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingHotKey)
                return; // 划词翻译快捷键正在录入，忽略

            if (_isCapturingQuickLookupHotKey)
            {
                StopQuickLookupHotKeyCapture();
                return;
            }

            _isCapturingQuickLookupHotKey = true;
            ChangeQuickLookupHotKeyButton.Content = "取消";
            QuickLookupHotKeyCaptureHint.Visibility = Visibility.Visible;
            QuickLookupHotKeyDisplayText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00));

            this.PreviewKeyDown += QuickLookupHotKey_KeyDown;
        }

        /// <summary>
        /// 快速查词快捷键录入键盘事件
        /// </summary>
        private void QuickLookupHotKey_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_isCapturingQuickLookupHotKey) return;

            // 忽略单独的修饰键
            if (e.Key == System.Windows.Input.Key.LeftCtrl || e.Key == System.Windows.Input.Key.RightCtrl ||
                e.Key == System.Windows.Input.Key.LeftAlt || e.Key == System.Windows.Input.Key.RightAlt ||
                e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift)
            {
                return;
            }

            var vk = (byte)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
            var requireAlt = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
            var requireCtrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            var requireShift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);

            if (!requireAlt && !requireCtrl && !requireShift)
            {
                MessageBox.Show("快捷键必须包含 Ctrl、Alt 或 Shift 中的至少一个修饰键", "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.QuickLookupHotKeyVK = vk;
            _settings.QuickLookupHotKeyRequireAlt = requireAlt;
            _settings.QuickLookupHotKeyRequireCtrl = requireCtrl;
            _settings.QuickLookupHotKeyRequireShift = requireShift;

            UpdateQuickLookupHotKeyDisplay();
            StopQuickLookupHotKeyCapture();
            _isDirty = true;

            e.Handled = true;
        }

        /// <summary>
        /// 停止快速查词快捷键录入
        /// </summary>
        private void StopQuickLookupHotKeyCapture()
        {
            _isCapturingQuickLookupHotKey = false;
            ChangeQuickLookupHotKeyButton.Content = "修改";
            QuickLookupHotKeyCaptureHint.Visibility = Visibility.Collapsed;
            QuickLookupHotKeyDisplayText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0));

            this.PreviewKeyDown -= QuickLookupHotKey_KeyDown;
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查…";

            try
            {
                // 结果只回给本次调用，无静态事件订阅，因此窗口关闭后不会泄漏
                var result = await UpdateService.CheckAsync(autoShowUpdateForm: true);
                if (!IsLoaded) return; // 窗口已关闭，不再触碰 UI

                UpdateStatusText.Text = result.Outcome switch
                {
                    UpdateCheckOutcome.UpToDate => "已是最新版本",
                    UpdateCheckOutcome.UpdateAvailable => $"发现新版本 {result.NewVersion}",
                    UpdateCheckOutcome.Error => "检查失败，请确认网络后重试",
                    UpdateCheckOutcome.Timeout => "长时间无响应，请稍后重试",
                    UpdateCheckOutcome.Skipped => "已有检查或更新窗口在进行中",
                    _ => ""
                };
            }
            catch (Exception ex)
            {
                Logger.Warn("Update", "update.settings_check_failed",
                    new { error_type = ex.GetType().Name });
                if (IsLoaded) UpdateStatusText.Text = "检查失败，请稍后重试";
            }
            finally
            {
                // 无论走哪条分支按钮都会恢复，不存在卡在禁用态的可能
                if (IsLoaded) CheckUpdateButton.IsEnabled = true;
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

        private sealed record AnalysisPromptChoice(string Id, string Name, bool IsBuiltIn);
    }
}

