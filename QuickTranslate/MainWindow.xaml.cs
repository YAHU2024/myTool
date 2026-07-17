using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings = null!;
        private OpenAITranslationService _translationService = null!;
        private bool _isInitializing = true;

        public MainWindow()
        {
            InitializeComponent();
            InitializeApp();
        }

        private void InitializeApp()
        {
            // 加载配置
            _settings = ConfigManager.Load();

            // 初始化翻译服务
            _translationService = new OpenAITranslationService(_settings);

            // 填充语言下拉框
            LanguageComboBox.ItemsSource = _settings.SupportedLanguages;
            LanguageComboBox.SelectedItem = _settings.TargetLanguage;

            // 填充设置区域
            ApiBaseUrlTextBox.Text = _settings.ApiBaseUrl;
            ApiKeyTextBox.Text = _settings.ApiKey;

            // 填充模型下拉框（已保存的配置组合）
            RefreshModelComboBox();

            _isInitializing = false;
            UpdateStatus("就绪");
        }

        /// <summary>
        /// 从 URL 提取域名简称
        /// </summary>
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

        /// <summary>
        /// 刷新模型下拉框（按域名分组显示）
        /// </summary>
        private void RefreshModelComboBox()
        {
            ModelComboBox.Items.Clear();

            // 按域名分组
            var groups = _settings.SavedConfigs
                .GroupBy(c => ExtractDomainShortName(c.ApiBaseUrl))
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                // 分组标题（不可选中）
                var separator = new ComboBoxItem
                {
                    Content = $"── {group.Key} ──",
                    IsEnabled = false,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99)),
                    FontSize = 11
                };
                ModelComboBox.Items.Add(separator);

                // 该分组下的模型（仅显示模型名称）
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

            // 设置当前模型名称
            ModelComboBox.Text = _settings.ModelName;
        }

        private async void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            await DoTranslate();
        }

        private async Task DoTranslate()
        {
            var sourceText = SourceTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                ResultTextBox.Text = "";
                UpdateStatus("请输入要翻译的文本");
                return;
            }

            var targetLang = LanguageComboBox.SelectedItem?.ToString() ?? _settings.TargetLanguage;

            TranslateButton.IsEnabled = false;
            UpdateStatus("翻译中...");
            ResultTextBox.Text = "";

            try
            {
                var result = await _translationService.TranslateStreamingAsync(
                    sourceText,
                    targetLang,
                    chunk => Dispatcher.Invoke(() =>
                    {
                        ResultTextBox.Text = chunk;
                        ResultTextBox.ScrollToEnd();
                    }));

                UpdateStatus($"翻译完成 ({result.Length} 字)");
            }
            catch (InvalidOperationException ex)
            {
                ResultTextBox.Text = "";
                UpdateStatus($"配置错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                ResultTextBox.Text = "";
                UpdateStatus($"翻译失败: {ex.Message}");
            }
            finally
            {
                TranslateButton.IsEnabled = true;
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && LanguageComboBox.SelectedItem != null && !_isInitializing)
            {
                _settings.TargetLanguage = LanguageComboBox.SelectedItem?.ToString() ?? _settings.TargetLanguage;
                ConfigManager.Save(_settings);
            }
        }

        private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 可用于实时翻译触发，第一期暂不启用
        }

        /// <summary>
        /// 模型下拉框选择变化：自动填充对应的 URL 和 Key
        /// </summary>
        private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _settings == null) return;

            if (ModelComboBox.SelectedItem is ComboBoxItem cbi && cbi.Tag is SavedConfig config)
            {
                // 选中已保存的配置，自动填充所有字段
                _settings.ModelName = config.ModelName;
                _settings.ApiBaseUrl = config.ApiBaseUrl;
                _settings.ApiKey = config.ApiKey;

                ApiBaseUrlTextBox.Text = config.ApiBaseUrl;
                ApiKeyTextBox.Text = config.ApiKey;

                // 确保 ComboBox 文本显示为纯模型名称
                ModelComboBox.Text = config.ModelName;

                _translationService.UpdateSettings(_settings);
                ConfigManager.Save(_settings);
                UpdateStatus($"已切换至 {config.ModelName}");
            }
        }

        /// <summary>
        /// 保存当前配置到"最近使用"列表
        /// </summary>
        private void SaveCurrentConfig()
        {
            var model = ModelComboBox.Text?.Trim();
            var baseUrl = ApiBaseUrlTextBox.Text?.Trim() ?? "";
            var apiKey = ApiKeyTextBox.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(model)) return;

            _settings.ModelName = model;
            _settings.ApiBaseUrl = baseUrl;
            _settings.ApiKey = apiKey;

            // 去重：移除已存在的相同配置
            _settings.SavedConfigs.RemoveAll(c =>
                c.ModelName == model && c.ApiBaseUrl == baseUrl && c.ApiKey == apiKey);

            // 插入到最前面
            _settings.SavedConfigs.Insert(0, new SavedConfig
            {
                DisplayName = model,
                ModelName = model,
                ApiBaseUrl = baseUrl,
                ApiKey = apiKey
            });

            // 最多保留 10 个
            while (_settings.SavedConfigs.Count > 10)
                _settings.SavedConfigs.RemoveAt(_settings.SavedConfigs.Count - 1);

            _translationService.UpdateSettings(_settings);
            ConfigManager.Save(_settings);

            // 刷新下拉框
            RefreshModelComboBox();
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentConfig();
            UpdateStatus("设置已保存");
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }
    }
}
