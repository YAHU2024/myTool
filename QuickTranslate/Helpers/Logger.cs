using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace QuickTranslate.Helpers
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Fatal = 4
    }

    /// <summary>
    /// 轻量级文件日志器 - 零外部依赖，后台异步写入，按天轮转 + 大小限制 + 自动清理。
    /// 线程安全：任何线程（钩子线程、UI线程、STA工作线程）均可直接调用。
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// 日志目录路径（供退出追踪等场景使用）
        /// </summary>
        public static string LogDirectory => LogDir;

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickTranslate", "logs");

        private static readonly ConcurrentQueue<string> _queue = new();
        private static readonly ManualResetEventSlim _signal = new(false);
        private static Thread? _writerThread;
        private static volatile bool _isRunning;
        private static LogLevel _minLevel = LogLevel.Info;
        private static int _retentionDays = 7;

        private const long MaxFileSize = 5 * 1024 * 1024; // 5MB
        private static string _currentDate = "";
        private static int _fileIndex;

        /// <summary>
        /// 初始化日志系统（在 App.OnStartup 中调用）
        /// </summary>
        public static void Init(LogLevel minLevel = LogLevel.Info, int retentionDays = 7)
        {
            _minLevel = minLevel;
            _retentionDays = retentionDays;
            _isRunning = true;

            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);

            // 清理过期日志
            CleanupExpiredLogs();

            // 启动后台写入线程
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LogWriter"
            };
            _writerThread.Start();
        }

        /// <summary>
        /// 关闭日志系统，确保残余日志写入磁盘（在 App.OnExit 中调用）
        /// </summary>
        public static void Shutdown()
        {
            _isRunning = false;
            _signal.Set(); // 唤醒写入线程
            _writerThread?.Join(2000);
            FlushQueue(); // 最终兜底写入
        }

        public static void Debug(string source, string message)
            => Enqueue(LogLevel.Debug, source, message);

        public static void Info(string source, string message)
            => Enqueue(LogLevel.Info, source, message);

        public static void Warn(string source, string message)
            => Enqueue(LogLevel.Warn, source, message);

        public static void Error(string source, string message, Exception? ex = null)
            => Enqueue(LogLevel.Error, source, FormatException(message, ex));

        public static void Fatal(string source, string message, Exception? ex = null)
        {
            Enqueue(LogLevel.Fatal, source, FormatException(message, ex));
            // Fatal 立即同步刷盘，防止崩溃前日志丢失
            _signal.Set();
            FlushQueue();
        }

        /// <summary>
        /// 解析配置中的日志级别字符串
        /// </summary>
        public static LogLevel ParseLevel(string level)
        {
            return level?.ToLowerInvariant() switch
            {
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Info,
                "warn" => LogLevel.Warn,
                "error" => LogLevel.Error,
                "fatal" => LogLevel.Fatal,
                _ => LogLevel.Info
            };
        }

        // ==================== 内部实现 ====================

        private static void Enqueue(LogLevel level, string source, string message)
        {
            if (level < _minLevel) return;

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var levelTag = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warn => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Fatal => "FTL",
                _ => "???"
            };

            var line = $"{timestamp} [{levelTag}] [{source}] {message}";
            _queue.Enqueue(line);

#if DEBUG
            // Debug 构建同时输出到终端，方便 dotnet run 时实时查看
            Console.WriteLine(line);
#endif

            // 队列积压较多时立即唤醒写入线程
            if (_queue.Count >= 50)
                _signal.Set();
        }

        private static string FormatException(string message, Exception? ex)
        {
            if (ex == null) return message;
            return $"{message} | {ex.GetType().Name}: {ex.Message}";
        }

        /// <summary>
        /// 后台写入循环：每 500ms 或收到信号时批量写入
        /// </summary>
        private static void WriterLoop()
        {
            while (_isRunning)
            {
                _signal.Wait(500);
                _signal.Reset();
                FlushQueue();
            }
        }

        /// <summary>
        /// 将队列中的日志批量写入文件
        /// </summary>
        private static void FlushQueue()
        {
            if (_queue.IsEmpty) return;

            try
            {
                var filePath = GetLogFilePath();
                using var writer = new StreamWriter(
                    new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read));

                while (_queue.TryDequeue(out var line))
                {
                    writer.WriteLine(line);
                }
            }
            catch
            {
                // 日志写入失败不应影响主程序，静默丢弃
            }
        }

        /// <summary>
        /// 获取当前日志文件路径（按天分文件 + 大小轮转）
        /// </summary>
        private static string GetLogFilePath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            // 日期变更，重置文件序号
            if (_currentDate != today)
            {
                _currentDate = today;
                _fileIndex = 0;
            }

            // 检查当前文件大小，超出则递增序号
            var path = BuildPath(today, _fileIndex);
            if (File.Exists(path) && new FileInfo(path).Length >= MaxFileSize)
            {
                _fileIndex++;
                path = BuildPath(today, _fileIndex);
            }

            return path;
        }

        private static string BuildPath(string date, int index)
        {
            var name = index == 0
                ? $"quicktranslate-{date}.log"
                : $"quicktranslate-{date}-{index}.log";
            return Path.Combine(LogDir, name);
        }

        /// <summary>
        /// 清理过期日志文件
        /// </summary>
        private static void CleanupExpiredLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-_retentionDays);
                var files = Directory.GetFiles(LogDir, "quicktranslate-*.log");

                foreach (var file in files)
                {
                    if (File.GetCreationTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // 清理失败不影响主程序
            }
        }
    }
}
