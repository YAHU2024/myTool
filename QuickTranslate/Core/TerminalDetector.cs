using System.Diagnostics;
using System.Text;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

internal enum TerminalRiskKind
{
    NonTerminal,
    KnownTerminal,
    EmbeddedTerminal,
    SuspectedTerminal
}

internal enum CopyDecisionReason
{
    OrdinaryApplication,
    ExplicitTerminalMapping,
    WindowsTerminalSafeDefault,
    CompatibleTerminalShortcut,
    TerminalCaptureDisabled,
    TerminalShortcutNotConfigured,
    ForegroundUnavailable
}

internal sealed record ForegroundWindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string WindowClassName = "",
    string FocusedAutomationId = "",
    string FocusedClassName = "",
    string FocusedControlType = "",
    bool FocusMetadataAvailable = false);

internal sealed record CopyRequest(
    IntPtr ExpectedForegroundWindow,
    CopyShortcut Shortcut,
    bool RestoreClipboard,
    TerminalRiskKind TerminalRisk,
    CopyDecisionReason DecisionReason,
    bool HasVerifiedSelection = false);

internal sealed record TerminalCopyDecision(
    bool IsAllowed,
    TerminalRiskKind Risk,
    CopyDecisionReason Reason,
    CopyShortcut? Shortcut,
    bool RestoreClipboard,
    string? RejectionMessage);

internal static class TerminalDetector
{
    private static readonly HashSet<string> KnownTerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "OpenConsole",
        "conhost",
        "cmd",
        "powershell",
        "pwsh",
        "wezterm",
        "wezterm-gui",
        "alacritty",
        "mintty",
        "ConEmu",
        "ConEmu64",
        "Hyper",
        "Tabby",
        "FluentTerminal"
    };

    private static readonly HashSet<string> KnownTerminalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "mintty",
        "VirtualConsoleClass"
    };

    private static readonly HashSet<string> EmbeddedTerminalHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code",
        "Code - Insiders",
        "VSCodium",
        "Cursor",
        "Windsurf",
        "Codex"
    };

    private static readonly string[] TerminalFocusMarkers =
    {
        "terminal",
        "xterm",
        "console"
    };

    private static readonly string[] EditorFocusMarkers =
    {
        "editor",
        "monaco"
    };

    internal static ForegroundWindowInfo? CaptureForegroundWindow()
    {
        try
        {
            var hwnd = Win32Api.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            Win32Api.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
                return null;

            return new ForegroundWindowInfo(
                hwnd,
                processId,
                Process.GetProcessById((int)processId).ProcessName,
                GetWindowClassName(hwnd));
        }
        catch (Exception ex)
        {
            Logger.Debug("TerminalDetector", "foreground.capture_failed", new { error_type = ex.GetType().Name });
            return null;
        }
    }

    internal static async Task<ForegroundWindowInfo?> CaptureForegroundWindowWithFocusAsync(
        int timeoutMs = 350,
        CancellationToken cancellationToken = default)
    {
        var target = CaptureForegroundWindow();
        if (target == null)
            return null;

        if (!EmbeddedTerminalHostProcesses.Contains(NormalizeProcessName(target.ProcessName)))
            return target;

        var focus = await SelectionLocator.TryGetFocusedAutomationContextAsync(timeoutMs, cancellationToken);
        if (Win32Api.GetForegroundWindow() != target.Handle)
            return null;

        if (focus == null)
            return target;

        return target with
        {
            FocusedAutomationId = focus.AutomationId,
            FocusedClassName = focus.ClassName,
            FocusedControlType = focus.ControlType,
            FocusMetadataAvailable = true
        };
    }

    internal static TerminalCopyDecision EvaluateCopyPolicy(ForegroundWindowInfo? target, AppSettings settings)
    {
        if (target == null)
        {
            return Reject(
                TerminalRiskKind.SuspectedTerminal,
                CopyDecisionReason.ForegroundUnavailable,
                "无法确认选中文本所在的窗口");
        }

        var mappings = ParseMappings(settings.TerminalCopyMappings);
        var hasMapping = mappings.TryGetValue(NormalizeProcessName(target.ProcessName), out var mappedShortcut);
        var risk = Classify(target, hasMapping);
        if (risk == TerminalRiskKind.NonTerminal)
        {
            return Allow(
                risk,
                CopyDecisionReason.OrdinaryApplication,
                CopyShortcut.CtrlC,
                restoreClipboard: true);
        }

        var mode = NormalizeMode(settings.TerminalCopyMode);
        if (mode == "Disabled")
        {
            return Reject(
                risk,
                CopyDecisionReason.TerminalCaptureDisabled,
                "终端取词已在设置中关闭");
        }

        if (hasMapping)
        {
            return Allow(
                risk,
                CopyDecisionReason.ExplicitTerminalMapping,
                mappedShortcut!,
                restoreClipboard: false);
        }

        if (NormalizeProcessName(target.ProcessName).Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
            target.WindowClassName.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase))
        {
            return Allow(
                risk,
                CopyDecisionReason.WindowsTerminalSafeDefault,
                CopyShortcut.CtrlShiftC,
                restoreClipboard: false);
        }

        if (mode == "Compatible" && risk == TerminalRiskKind.KnownTerminal)
        {
            return Allow(
                risk,
                CopyDecisionReason.CompatibleTerminalShortcut,
                CopyShortcut.CtrlShiftC,
                restoreClipboard: false);
        }

        return Reject(
            risk,
            CopyDecisionReason.TerminalShortcutNotConfigured,
            $"未为 {target.ProcessName} 配置安全复制快捷键");
    }

    internal static bool ShouldSuppressSelection(ForegroundWindowInfo target, AppSettings settings)
    {
        var decision = EvaluateCopyPolicy(target, settings);
        return decision.Reason == CopyDecisionReason.TerminalCaptureDisabled;
    }

    internal static bool RequiresVerifiedSelection(ForegroundWindowInfo target, AppSettings settings) =>
        EvaluateCopyPolicy(target, settings).Risk != TerminalRiskKind.NonTerminal;

    internal static bool TryCreateCopyRequest(
        ForegroundWindowInfo? target,
        AppSettings settings,
        out CopyRequest? request,
        out string? rejectionMessage)
    {
        var decision = EvaluateCopyPolicy(target, settings);
        LogDecision(target, settings, decision);

        request = null;
        rejectionMessage = decision.RejectionMessage;
        if (!decision.IsAllowed || target == null || decision.Shortcut == null)
            return false;

        request = new CopyRequest(
            target.Handle,
            decision.Shortcut,
            decision.RestoreClipboard,
            decision.Risk,
            decision.Reason);
        return true;
    }

    private static TerminalRiskKind Classify(ForegroundWindowInfo target, bool hasMapping)
    {
        if (hasMapping || KnownTerminalProcesses.Contains(NormalizeProcessName(target.ProcessName)))
            return TerminalRiskKind.KnownTerminal;

        if (KnownTerminalWindowClasses.Contains(target.WindowClassName))
            return TerminalRiskKind.KnownTerminal;

        if (EmbeddedTerminalHostProcesses.Contains(NormalizeProcessName(target.ProcessName)))
        {
            if (!target.FocusMetadataAvailable)
                return TerminalRiskKind.SuspectedTerminal;

            if (ContainsTerminalMarker(target.FocusedAutomationId) ||
                ContainsTerminalMarker(target.FocusedClassName) ||
                ContainsTerminalMarker(target.FocusedControlType))
            {
                return TerminalRiskKind.EmbeddedTerminal;
            }

            if (ContainsEditorMarker(target.FocusedAutomationId) ||
                ContainsEditorMarker(target.FocusedClassName))
            {
                return TerminalRiskKind.NonTerminal;
            }

            return TerminalRiskKind.SuspectedTerminal;
        }

        return TerminalRiskKind.NonTerminal;
    }

    private static bool ContainsTerminalMarker(string value) =>
        TerminalFocusMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsEditorMarker(string value) =>
        EditorFocusMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, CopyShortcut> ParseMappings(string? raw)
    {
        var result = new Dictionary<string, CopyShortcut>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
                continue;

            if (CopyShortcut.TryParse(pair[1], out var parsed))
                result[NormalizeProcessName(pair[0])] = parsed;
        }

        return result;
    }

    private static string NormalizeProcessName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

    private static string NormalizeMode(string? mode) => mode?.Trim() switch
    {
        { } value when value.Equals("Disabled", StringComparison.OrdinalIgnoreCase) => "Disabled",
        { } value when value.Equals("Compatible", StringComparison.OrdinalIgnoreCase) => "Compatible",
        _ => "Smart"
    };

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        return Win32Api.GetClassName(hwnd, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static TerminalCopyDecision Allow(
        TerminalRiskKind risk,
        CopyDecisionReason reason,
        CopyShortcut shortcut,
        bool restoreClipboard) =>
        new(true, risk, reason, shortcut, restoreClipboard, null);

    private static TerminalCopyDecision Reject(
        TerminalRiskKind risk,
        CopyDecisionReason reason,
        string rejectionMessage) =>
        new(false, risk, reason, null, false, rejectionMessage);

    private static void LogDecision(
        ForegroundWindowInfo? target,
        AppSettings settings,
        TerminalCopyDecision decision)
    {
        Logger.Debug(
            "TerminalDetector",
            "terminal.copy_decision",
            BuildDecisionLogContext(target, settings, decision));
    }

    internal static IReadOnlyDictionary<string, object?> BuildDecisionLogContext(
        ForegroundWindowInfo? target,
        AppSettings settings,
        TerminalCopyDecision decision) =>
        new Dictionary<string, object?>
        {
            ["process_name"] = target?.ProcessName ?? string.Empty,
            ["window_class"] = target?.WindowClassName ?? string.Empty,
            ["focus_metadata_available"] = target?.FocusMetadataAvailable ?? false,
            ["focused_automation_id"] = target?.FocusedAutomationId ?? string.Empty,
            ["focused_class"] = target?.FocusedClassName ?? string.Empty,
            ["focused_control_type"] = target?.FocusedControlType ?? string.Empty,
            ["mode"] = NormalizeMode(settings.TerminalCopyMode),
            ["terminal_risk"] = decision.Risk.ToString(),
            ["decision"] = decision.Reason.ToString(),
            ["shortcut"] = decision.Shortcut?.ToString() ?? string.Empty
        };
}
