using System;
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
            ModelNameTextBox.Text = _settings.ModelName;

            UpdateStatus("就绪");
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
                // 使用流式翻译，逐字显示结果
                var result = await _translationService.TranslateStreamingAsync(
                    sourceText,
                    targetLang,
                    // 回调在后台线程执行，需通过 Dispatcher 切换到 UI 线程
                    chunk => Dispatcher.Invoke(() =>
                    {
                        ResultTextBox.Text = chunk;
                        // 自动滚动到底部
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
            if (_settings != null && LanguageComboBox.SelectedItem != null)
            {
                _settings.TargetLanguage = LanguageComboBox.SelectedItem?.ToString() ?? _settings.TargetLanguage;
                ConfigManager.Save(_settings);
            }
        }

        private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 可用于实时翻译触发，第一期暂不启用
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.ApiBaseUrl = ApiBaseUrlTextBox.Text?.Trim() ?? _settings.ApiBaseUrl;
            _settings.ApiKey = ApiKeyTextBox.Text?.Trim() ?? _settings.ApiKey;
            _settings.ModelName = ModelNameTextBox.Text?.Trim() ?? _settings.ModelName;

            _translationService.UpdateSettings(_settings);
            ConfigManager.Save(_settings);

            UpdateStatus("设置已保存");
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }
    }
}
