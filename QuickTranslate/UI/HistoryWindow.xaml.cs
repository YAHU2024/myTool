using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using QuickTranslate.Database;

namespace QuickTranslate.UI
{
    /// <summary>
    /// 翻译历史查看窗口
    /// </summary>
    public partial class HistoryWindow : Window
    {
        private const int PageSize = 50;
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalRecords = 0;
        private List<TranslationRecord> _currentPageData = new();

        // 筛选条件
        private string _searchText = string.Empty;
        private string _languageFilter = string.Empty;
        private int _timeRangeDays = 0; // 0 = 全部

        public HistoryWindow()
        {
            InitializeComponent();
            InitializeFilters();
            LoadData();
        }

        /// <summary>
        /// 初始化筛选器
        /// </summary>
        private void InitializeFilters()
        {
            // 语言筛选
            var languages = new List<string> { "全部语言" };
            using var context = new TranslationDbContext();
            var langPairs = context.TranslationRecords
                .Select(r => r.TargetLanguage)
                .Distinct()
                .OrderBy(l => l)
                .ToList();
            languages.AddRange(langPairs);
            LanguageFilterComboBox.ItemsSource = languages;
            LanguageFilterComboBox.SelectedIndex = 0;

            // 时间范围筛选
            TimeRangeComboBox.ItemsSource = new[]
            {
                "全部时间",
                "最近 7 天",
                "最近 30 天",
                "最近 90 天"
            };
            TimeRangeComboBox.SelectedIndex = 0;
        }

        /// <summary>
        /// 加载数据
        /// </summary>
        private void LoadData()
        {
            using var context = new TranslationDbContext();
            var query = context.TranslationRecords.AsQueryable();

            // 应用筛选
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                query = query.Where(r =>
                    r.SourceText.Contains(_searchText) ||
                    r.Translation.Contains(_searchText));
            }

            if (!string.IsNullOrWhiteSpace(_languageFilter) && _languageFilter != "全部语言")
            {
                query = query.Where(r => r.TargetLanguage == _languageFilter);
            }

            if (_timeRangeDays > 0)
            {
                var startDate = DateTime.Now.AddDays(-_timeRangeDays);
                query = query.Where(r => r.TranslatedAt >= startDate);
            }

            // 统计总数
            _totalRecords = query.Count();
            _totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalRecords / PageSize));

            // 确保当前页在有效范围内
            if (_currentPage > _totalPages)
                _currentPage = _totalPages;
            if (_currentPage < 1)
                _currentPage = 1;

            // 分页查询
            _currentPageData = query
                .OrderByDescending(r => r.TranslatedAt)
                .Skip((_currentPage - 1) * PageSize)
                .Take(PageSize)
                .ToList();

            // 更新 UI
            HistoryDataGrid.ItemsSource = _currentPageData;
            UpdatePaginationUI();
        }

        /// <summary>
        /// 更新分页 UI
        /// </summary>
        private void UpdatePaginationUI()
        {
            StatsTextBlock.Text = $"共 {_totalRecords} 条记录";
            PageInfoTextBlock.Text = $"第 {_currentPage} / {_totalPages} 页";

            FirstPageButton.IsEnabled = _currentPage > 1;
            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < _totalPages;
            LastPageButton.IsEnabled = _currentPage < _totalPages;
        }

        // ==================== 事件处理 ====================

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _searchText = SearchTextBox.Text?.Trim() ?? string.Empty;
            _languageFilter = LanguageFilterComboBox.SelectedItem?.ToString() ?? string.Empty;

            var timeRangeIndex = TimeRangeComboBox.SelectedIndex;
            _timeRangeDays = timeRangeIndex switch
            {
                1 => 7,
                2 => 30,
                3 => 90,
                _ => 0
            };

            _currentPage = 1;
            LoadData();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = string.Empty;
            LanguageFilterComboBox.SelectedIndex = 0;
            TimeRangeComboBox.SelectedIndex = 0;

            _searchText = string.Empty;
            _languageFilter = string.Empty;
            _timeRangeDays = 0;
            _currentPage = 1;

            LoadData();
        }

        private void FirstPageButton_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            LoadData();
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                LoadData();
            }
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                LoadData();
            }
        }

        private void LastPageButton_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = _totalPages;
            LoadData();
        }

        /// <summary>
        /// 双击行 - 复制原文或译文
        /// </summary>
        private void HistoryDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (HistoryDataGrid.SelectedItem is TranslationRecord record)
            {
                try
                {
                    Clipboard.SetText(record.Translation);
                    MessageBox.Show("译文已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 导出为 Anki 格式（CSV/TSV）
        /// </summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出翻译历史",
                Filter = "CSV 文件 (*.csv)|*.csv|TSV 文件 (*.tsv)|*.tsv|文本文件 (*.txt)|*.txt",
                DefaultExt = ".csv",
                FileName = $"QuickTranslate_History_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    ExportHistory(dialog.FileName);
                    MessageBox.Show($"导出成功！\n文件: {dialog.FileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 导出历史记录到文件
        /// </summary>
        private void ExportHistory(string filePath)
        {
            using var context = new TranslationDbContext();
            var query = context.TranslationRecords.AsQueryable();

            // 应用当前筛选条件
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                query = query.Where(r =>
                    r.SourceText.Contains(_searchText) ||
                    r.Translation.Contains(_searchText));
            }

            if (!string.IsNullOrWhiteSpace(_languageFilter) && _languageFilter != "全部语言")
            {
                query = query.Where(r => r.TargetLanguage == _languageFilter);
            }

            if (_timeRangeDays > 0)
            {
                var startDate = DateTime.Now.AddDays(-_timeRangeDays);
                query = query.Where(r => r.TranslatedAt >= startDate);
            }

            var records = query.OrderByDescending(r => r.TranslatedAt).ToList();

            // 判断文件类型
            var extension = Path.GetExtension(filePath).ToLower();
            var separator = extension == ".tsv" ? "\t" : ",";

            var sb = new StringBuilder();

            // 写入表头（Anki 格式：原文、译文、源语言、目标语言、时间）
            sb.AppendLine($"原文{separator}译文{separator}源语言{separator}目标语言{separator}时间");

            // 写入数据
            foreach (var record in records)
            {
                var source = EscapeCsvField(record.SourceText, separator);
                var translation = EscapeCsvField(record.Translation, separator);
                var sourceLang = EscapeCsvField(record.SourceLanguage, separator);
                var targetLang = EscapeCsvField(record.TargetLanguage, separator);
                var time = record.TranslatedAt.ToString("yyyy-MM-dd HH:mm:ss");

                sb.AppendLine($"{source}{separator}{translation}{separator}{sourceLang}{separator}{targetLang}{separator}{time}");
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// CSV 字段转义（处理换行和引号）
        /// </summary>
        private static string EscapeCsvField(string field, string separator)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            // 如果包含分隔符、换行符或引号，需要用引号包裹
            if (field.Contains(separator) || field.Contains("\n") || field.Contains("\r") || field.Contains("\""))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }
    }
}
