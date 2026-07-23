using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 浏览器进程检测器 - 判断当前前台窗口是否为浏览器，
    /// 用于在浏览器中禁用翻译以避免与浏览器翻译插件冲突。
    /// </summary>
    public static class BrowserDetector
    {
        /// <summary>
        /// 内置已知浏览器进程名（小写）
        /// </summary>
        private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",
            "msedge",
            "firefox",
            "brave",
            "opera",
            "vivaldi",
            "arc",
            "waterfox",
            "floorp",
            "zen-browser",
            "iexplore",       // IE（遗留）
            "microsoftedge",  // 旧版 Edge
        };

        /// <summary>
        /// 判断当前前台窗口是否为浏览器进程
        /// </summary>
        /// <param name="customBrowserProcesses">用户自定义的浏览器进程名（逗号分隔）</param>
        /// <returns>true 表示前台是浏览器</returns>
        public static bool IsForegroundBrowser(string? customBrowserProcesses = null)
        {
            try
            {
                var hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;

                Win32Api.GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == 0) return false;

                var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName; // 不含 .exe

                // 检查内置列表
                if (KnownBrowsers.Contains(processName))
                {
                    Logger.Debug("BrowserDetector", $"前台为已知浏览器: {processName}");
                    return true;
                }

                // 检查用户自定义列表
                if (!string.IsNullOrWhiteSpace(customBrowserProcesses))
                {
                    var customList = customBrowserProcesses
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase));

                    foreach (var name in customList)
                    {
                        if (!string.IsNullOrWhiteSpace(name) &&
                            string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Debug("BrowserDetector", $"前台为用户自定义浏览器: {processName}");
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                // 进程可能已退出，忽略
                Logger.Debug("BrowserDetector", "foreground.detect_failed", new { error_type = ex.GetType().Name });
                return false;
            }
        }
    }
}
