using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;

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
        private bool _origAutoDetectLanguage = true;
        private bool _origSmartContentType = false;
        private string _origFallbackLanguage = "English";
        private string _origCustomTranslationPrompt = string.Empty;
        private byte _origHotKeyVK = 0x51;
        private bool _origHotKeyRequireAlt = true;
        private bool _origHotKeyRequireCtrl = false;
        private bool _origHotKeyRequireShift = false;
        private bool _origHotKeyEnabled = true;
        private bool _origEnableInBrowser = true;
        private string _origCustomBrowserProcesses = string.Empty;

        private readonly List<AnalysisPromptProfile> _analysisPromptProfiles = new();
        private string _selectedAnalysisPromptId = AnalysisPromptCatalog.GeneralId;
        private string? _editingAnalysisPromptId;

        // 快捷键录入状态
        private bool _isCapturingHotKey = false;

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
            _origAutoDetectLanguage = _settings.AutoDetectLanguage;
            _origSmartContentType = _settings.SmartContentType;
            _origFallbackLanguage = _settings.FallbackLanguage;
            _origCustomTranslationPrompt = _settings.CustomTranslationPrompt;
            _origHotKeyVK = _settings.HotKeyVK;
            _origHotKeyRequireAlt = _settings.HotKeyRequireAlt;
            _origHotKeyRequireCtrl = _settings.HotKeyRequireCtrl;
            _origHotKeyRequireShift = _settings.HotKeyRequireShift;
            _origHotKeyEnabled = _settings.HotKeyEnabled;
            _origEnableInBrowser = _settings.EnableInBrowser;
            _origCustomBrowserProcesses = _settings.CustomBrowserProcesses;
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

            // 语言自动检测
            AutoDetectLanguageCheckBox.IsChecked = _settings.AutoDetectLanguage;

            // 备选语言
            FallbackLanguageComboBox.ItemsSource = _settings.SupportedLanguages;
            FallbackLanguageComboBox.SelectedItem = _settings.FallbackLanguage;

            // 智能内容识别
            SmartContentTypeCheckBox.IsChecked = _settings.SmartContentType;

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

            // 快捷键显示
            HotKeyEnabledCheckBox.IsChecked = _settings.HotKeyEnabled;
            UpdateHotKeyDisplay();

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
                0x41 => "A",
                0x5A => "Z",
                0x20 => "Space",
                _ => $"VK_{vk:X2}"
            };
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

            if (ModelComboBox.SelectedItem is ComboBoxItem cbi2 && cbi2.Tag is SavedConfig config)
            {
                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyPasswordBox.Password = config.ApiKey;
                ApiKeyVisibleTextBox.Text = config.ApiKey;

                // 显示绿色反馈
                var domain = ExtractDomainShortName(config.ApiBaseUrl);
                ModelFeedbackText.Text = $"已切换到 {config.ModelName}（{domain}）";
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
                $"确定要删除解析方案“{profile.Name}”吗？",
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

            _settings.SavedConfigs.Remove(config);
            DeleteConfigButton.IsEnabled = false;
            _isDirty = true;

            // 刷新模型下拉框
            RefreshModelComboBox();

            // 清空输入框
            ApiBaseUrlTextBox.Text = string.Empty;
            ApiKeyPasswordBox.Password = string.Empty;
            ApiKeyVisibleTextBox.Text = string.Empty;

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
            SaveAnalysisPromptEditor();
            if (!ValidateAnalysisPromptProfiles())
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

            _settings.AutoDetectLanguage = AutoDetectLanguageCheckBox.IsChecked ?? true;

            _settings.SmartContentType = SmartContentTypeCheckBox.IsChecked ?? false;

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

            _settings.HotKeyEnabled = HotKeyEnabledCheckBox.IsChecked ?? true;

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
                    $"解析方案名称“{duplicateName.Key}”已存在，请使用其他名称。",
                    "解析方案名称重复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
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
