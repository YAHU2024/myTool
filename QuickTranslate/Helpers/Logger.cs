using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace QuickTranslate.Helpers;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Fatal = 4
}

/// <summary>
/// Lightweight asynchronous JSONL logger. Legacy source/message calls remain supported.
/// </summary>
public static class Logger
{
    public static string LogDirectory => LogDir;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickTranslate", "logs");
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly ManualResetEventSlim Signal = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static Thread? _writerThread;
    private static volatile bool _isRunning;
    private static LogLevel _minLevel = LogLevel.Info;
    private static int _retentionDays = 7;
    private static long _maxTotalBytes = 50 * 1024 * 1024;
    private static string _currentDate = string.Empty;
    private static int _fileIndex;
    private const long MaxFileSize = 5 * 1024 * 1024;

    public static void Init(
        LogLevel minLevel = LogLevel.Info,
        int retentionDays = 7,
        long maxTotalBytes = 50 * 1024 * 1024)
    {
        _minLevel = minLevel;
        _retentionDays = Math.Clamp(retentionDays, 1, 3650);
        _maxTotalBytes = Math.Clamp(maxTotalBytes, 1 * 1024 * 1024, 1024L * 1024 * 1024);
        Directory.CreateDirectory(LogDir);
        CleanupLogs();
        _isRunning = true;

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "LogWriter"
        };
        _writerThread.Start();
    }

    public static void Shutdown()
    {
        _isRunning = false;
        Signal.Set();
        _writerThread?.Join(2000);
        FlushQueue();
    }

    public static void Configure(LogLevel minLevel, int retentionDays, long maxTotalBytes)
    {
        _minLevel = minLevel;
        _retentionDays = Math.Clamp(retentionDays, 1, 3650);
        _maxTotalBytes = Math.Clamp(maxTotalBytes, 1 * 1024 * 1024, 1024L * 1024 * 1024);
        CleanupLogs();
    }

    public static void Debug(string source, string message) => Enqueue(LogLevel.Debug, source, message, null);
    public static void Info(string source, string message) => Enqueue(LogLevel.Info, source, message, null);
    public static void Warn(string source, string message) => Enqueue(LogLevel.Warn, source, message, null);
    public static void Error(string source, string message, Exception? ex = null) =>
        Enqueue(LogLevel.Error, source, message, ExceptionContext(ex));
    public static void Fatal(string source, string message, Exception? ex = null)
    {
        Enqueue(LogLevel.Fatal, source, message, ExceptionContext(ex));
        Signal.Set();
        FlushQueue();
    }

    public static void Debug(string source, string eventName, object? context) => Enqueue(LogLevel.Debug, source, eventName, ToContext(context));
    public static void Info(string source, string eventName, object? context) => Enqueue(LogLevel.Info, source, eventName, ToContext(context));
    public static void Warn(string source, string eventName, object? context) => Enqueue(LogLevel.Warn, source, eventName, ToContext(context));
    public static void Error(string source, string eventName, object? context, Exception? ex = null) => Enqueue(LogLevel.Error, source, eventName, MergeContext(ToContext(context), ExceptionContext(ex)));
    public static void Fatal(string source, string eventName, object? context, Exception? ex = null)
    {
        Enqueue(LogLevel.Fatal, source, eventName, MergeContext(ToContext(context), ExceptionContext(ex)));
        Signal.Set();
        FlushQueue();
    }

    public static LogLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        "fatal" => LogLevel.Fatal,
        _ => LogLevel.Info
    };

    public static void CleanupLogs()
    {
        try
        {
            if (!Directory.Exists(LogDir))
                return;
            var protectedPaths = new List<string>();
            if (!string.IsNullOrEmpty(_currentDate))
                protectedPaths.Add(BuildPath(_currentDate, _fileIndex));
            if (_isRunning)
            {
                protectedPaths.Add(Path.Combine(LogDir, "shutdown-trace.log"));
                protectedPaths.Add(Path.Combine(LogDir, "watchdog.trace"));
            }
            CleanupDirectory(LogDir, _retentionDays, _maxTotalBytes, DateTime.UtcNow, protectedPaths);
        }
        catch
        {
            // Diagnostics must never affect the application.
        }
    }

    internal static void CleanupDirectory(
        string directory,
        int retentionDays,
        long maxTotalBytes,
        DateTime utcNow,
        IReadOnlyCollection<string>? protectedPaths = null)
    {
        if (!Directory.Exists(directory))
            return;
        var protectedSet = new HashSet<string>(
            (protectedPaths ?? Array.Empty<string>()).Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        var cutoff = utcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        var files = GetManagedFiles(directory).OrderBy(file => file.LastWriteTimeUtc).ToList();

        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff).ToArray())
        {
            if (!protectedSet.Contains(file.FullName))
                TryDelete(file.FullName);
        }

        files = GetManagedFiles(directory).OrderBy(file => file.LastWriteTimeUtc).ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= maxTotalBytes)
                break;
            if (protectedSet.Contains(file.FullName))
                continue;
            if (TryDelete(file.FullName))
                total -= file.Length;
        }
    }

    internal static string Serialize(LogEvent record) => JsonSerializer.Serialize(record, JsonOptions);

    internal static bool TryParse(string line, out LogEvent? record)
    {
        try
        {
            record = JsonSerializer.Deserialize<LogEvent>(line, JsonOptions);
            return record is not null;
        }
        catch
        {
            record = null;
            return false;
        }
    }

    private static void Enqueue(LogLevel level, string source, string eventName, IReadOnlyDictionary<string, object?>? context)
    {
        if (level < _minLevel)
            return;

        var record = new LogEvent(
            DateTimeOffset.Now,
            level,
            string.IsNullOrWhiteSpace(source) ? "Unknown" : source,
            string.IsNullOrWhiteSpace(eventName) ? "message" : eventName,
            context ?? new Dictionary<string, object?>());
        var line = Serialize(record);
        Queue.Enqueue(line);

#if DEBUG
        Console.WriteLine(line);
#endif
        if (Queue.Count >= 50)
            Signal.Set();
    }

    private static void WriterLoop()
    {
        while (_isRunning)
        {
            Signal.Wait(500);
            Signal.Reset();
            FlushQueue();
        }
    }

    private static void FlushQueue()
    {
        if (Queue.IsEmpty)
            return;

        try
        {
            var filePath = GetLogFilePath();
            using var writer = new StreamWriter(
                new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false));
            while (Queue.TryDequeue(out var line))
                writer.WriteLine(line);
        }
        catch
        {
            // Logging is best effort and must not break the app.
        }
    }

    private static string GetLogFilePath()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_currentDate != today)
        {
            _currentDate = today;
            _fileIndex = 0;
        }

        var path = BuildPath(today, _fileIndex);
        if (File.Exists(path) && new FileInfo(path).Length >= MaxFileSize)
            path = BuildPath(today, ++_fileIndex);
        return path;
    }

    private static string BuildPath(string date, int index) => Path.Combine(
        LogDir,
        index == 0 ? $"quicktranslate-{date}.log" : $"quicktranslate-{date}-{index}.log");

    private static IEnumerable<FileInfo> GetManagedFiles(string directory)
    {
        var paths = Directory.EnumerateFiles(directory, "quicktranslate-*.log")
            .Concat(Directory.EnumerateFiles(directory, "shutdown-trace.log"))
            .Concat(Directory.EnumerateFiles(directory, "watchdog.trace"));
        return paths
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    private static Dictionary<string, object?> ToContext(object? context)
    {
        if (context is null)
            return new Dictionary<string, object?>();
        if (context is IReadOnlyDictionary<string, object?> dictionary)
            return new Dictionary<string, object?>(dictionary);

        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(context));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone())
                : new Dictionary<string, object?> { ["value"] = document.RootElement.Clone() };
        }
        catch
        {
            return new Dictionary<string, object?> { ["context_error"] = "serialization_failed" };
        }
    }

    internal static Dictionary<string, object?>? ExceptionContext(Exception? ex) => ex is null
        ? null
        : new Dictionary<string, object?>
        {
            ["exception_type"] = ex.GetType().Name
        };

    private static Dictionary<string, object?> MergeContext(
        Dictionary<string, object?> context,
        Dictionary<string, object?>? extra)
    {
        if (extra is null)
            return context;
        foreach (var pair in extra)
            context[pair.Key] = pair.Value;
        return context;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };
}
