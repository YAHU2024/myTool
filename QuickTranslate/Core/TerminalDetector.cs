using System;
using System.Collections.Generic;
using System.Diagnostics;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Core
{
    public sealed record ForegroundWindowInfo(IntPtr Handle, uint ProcessId, string ProcessName);
    public sealed record CopyRequest(IntPtr ExpectedForegroundWindow, CopyShortcut Shortcut, bool RestoreClipboard);

    public static class TerminalDetector
    {
        private static readonly HashSet<string> KnownTerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh" };

        public static ForegroundWindowInfo? CaptureForegroundWindow()
        {
            try
            {
                var hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                Win32Api.GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == 0) return null;
                return new ForegroundWindowInfo(hwnd, processId, Process.GetProcessById((int)processId).ProcessName);
            }
            catch (Exception ex)
            {
                Logger.Debug("TerminalDetector", "foreground.capture_failed", new { error_type = ex.GetType().Name });
                return null;
            }
        }

        public static bool TryCreateCopyRequest(ForegroundWindowInfo? target, AppSettings settings, out CopyRequest? request, out string? rejectionMessage)
        {
            request = null; rejectionMessage = null;
            if (target == null) { rejectionMessage = "无法确认选中文本所在的窗口"; return false; }
            var mappings = ParseMappings(settings.TerminalCopyMappings);
            var isTerminal = KnownTerminalProcesses.Contains(target.ProcessName);
            var hasMapping = mappings.TryGetValue(target.ProcessName, out var shortcut);
            if (!isTerminal && !hasMapping)
            {
                request = new CopyRequest(target.Handle, CopyShortcut.CtrlC, true);
                return true;
            }
            var mode = settings.TerminalCopyMode ?? "Smart";
            if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            { rejectionMessage = "终端取词已在设置中关闭"; return false; }
            if (hasMapping)
            { request = new CopyRequest(target.Handle, shortcut!, false); return true; }
            if (target.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            { request = new CopyRequest(target.Handle, CopyShortcut.CtrlShiftC, false); return true; }
            if (mode.Equals("Compatible", StringComparison.OrdinalIgnoreCase))
            { request = new CopyRequest(target.Handle, CopyShortcut.CtrlShiftC, false); return true; }
            rejectionMessage = $"未为 {target.ProcessName} 配置安全复制快捷键";
            return false;
        }

        private static Dictionary<string, CopyShortcut> ParseMappings(string? raw)
        {
            var result = new Dictionary<string, CopyShortcut>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0])) continue;
                var processName = pair[0].Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                if (CopyShortcut.TryParse(pair[1], out var parsed)) result[processName] = parsed;
            }
            return result;
        }
    }
}
