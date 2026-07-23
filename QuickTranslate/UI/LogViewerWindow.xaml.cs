using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslate.Helpers;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

public partial class LogViewerWindow : Window
{
    private readonly TranslationMetrics _metrics;
    private readonly TranslationCacheService _cache;
    private string[] _files = Array.Empty<string>();
    private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();

    public LogViewerWindow(TranslationMetrics metrics, TranslationCacheService cache)
    {
        _metrics = metrics;
        _cache = cache;
        InitializeComponent();
        LevelComboBox.ItemsSource = new object[] { "全部", LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal };
        LevelComboBox.SelectedIndex = 0;
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        _files = LogEntryReader.GetLogFiles(Logger.LogDirectory).ToArray();
        LogFileComboBox.ItemsSource = _files.Select(Path.GetFileName).ToArray();
        if (_files.Length > 0)
            LogFileComboBox.SelectedIndex = 0;
        else
            ApplyFilters();
    }

    private async void LogFileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFileComboBox.SelectedIndex < 0 || LogFileComboBox.SelectedIndex >= _files.Length)
            return;
        var path = _files[LogFileComboBox.SelectedIndex];
        try
        {
            _entries = await Task.Run(() => LogEntryReader.Read(path));
            ApplyFilters();
        }
        catch (Exception exception)
        {
            Logger.Warn("LogViewer", "log.read_failed", new { error_type = exception.GetType().Name });
            StatusTextBlock.Text = "日志读取失败";
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilters();
    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        var level = LevelComboBox.SelectedItem is LogLevel selected ? selected : (LogLevel?)null;
        var search = SearchTextBox.Text?.Trim() ?? string.Empty;
        var filtered = _entries.Where(entry =>
            (!level.HasValue || entry.Level == level.Value) &&
            (search.Length == 0 || entry.DisplayText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        EntriesListView.ItemsSource = filtered;
        var snapshot = _metrics.GetSnapshot(_cache.Hits, _cache.Misses);
        StatusTextBlock.Text = $"显示 {filtered.Length}/{_entries.Count} 条 | 今日完成 {snapshot.Completed} | 平均 {snapshot.AverageMilliseconds:F0}ms | P95 {snapshot.P95Milliseconds:F0}ms | 缓存命中率 {snapshot.CacheHitRate:P0}";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshFiles();

    private void OpenDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Logger.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = Logger.LogDirectory,
            UseShellExecute = true
        });
    }

    private void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.CleanupLogs();
        RefreshFiles();
    }
}
