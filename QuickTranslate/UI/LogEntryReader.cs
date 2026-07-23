using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,
    string EventName,
    IReadOnlyDictionary<string, object?> Context,
    string RawLine)
{
    public string DisplayText => Context.Count == 0
        ? EventName
        : $"{EventName} | {string.Join(", ", Context.Select(pair => $"{pair.Key}={pair.Value}"))}";
}

public static class LogEntryReader
{
    private static readonly Regex LegacyPattern = new(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}\\.\\d{3}) \\[(?<level>DBG|INF|WRN|ERR|FTL)\\] \\[(?<source>[^]]+)\\] (?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> GetLogFiles(string directory, int maxFiles = 31)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith("quicktranslate-", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Equals("shutdown-trace.log", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).Equals("watchdog.trace", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(Math.Max(1, maxFiles))
            .ToArray();
    }

    public static IReadOnlyList<LogEntry> Read(string path, int maxLines = 5000)
    {
        if (!File.Exists(path))
            return Array.Empty<LogEntry>();

        var lines = File.ReadLines(path).TakeLast(Math.Max(1, maxLines));
        var entries = new List<LogEntry>();
        foreach (var line in lines)
        {
            if (Logger.TryParse(line, out var structured) && structured is not null)
            {
                entries.Add(new LogEntry(structured.Timestamp, structured.Level, structured.Source,
                    structured.EventName, structured.Context, line));
                continue;
            }

            var match = LegacyPattern.Match(line);
            if (!match.Success || !DateTimeOffset.TryParseExact(
                    match.Groups["timestamp"].Value,
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var timestamp))
            {
                entries.Add(new LogEntry(
                    File.GetLastWriteTime(path),
                    LogLevel.Info,
                    "Raw",
                    line.Length > 1000 ? line[..1000] : line,
                    new Dictionary<string, object?>(),
                    line));
                continue;
            }

            entries.Add(new LogEntry(
                timestamp,
                match.Groups["level"].Value switch
                {
                    "DBG" => LogLevel.Debug,
                    "WRN" => LogLevel.Warn,
                    "ERR" => LogLevel.Error,
                    "FTL" => LogLevel.Fatal,
                    _ => LogLevel.Info
                },
                match.Groups["source"].Value,
                match.Groups["message"].Value,
                new Dictionary<string, object?>(),
                line));
        }
        return entries;
    }
}
