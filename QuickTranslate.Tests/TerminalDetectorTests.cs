using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TerminalDetectorTests
{
    private static readonly IntPtr WindowHandle = new(42);

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("OpenConsole")]
    [InlineData("conhost")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void EvaluateCopyPolicy_DisabledRejectsKnownTerminal(string processName)
    {
        var decision = Evaluate(processName, mode: "Disabled");

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.KnownTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalCaptureDisabled, decision.Reason);
        Assert.Null(decision.Shortcut);
    }

    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    public void EvaluateCopyPolicy_DisabledRejectsTerminalWindowClass(string windowClass)
    {
        var decision = Evaluate("UnknownHost", mode: "Disabled", windowClass: windowClass);

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.KnownTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalCaptureDisabled, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_DisabledRejectsEmbeddedTerminalFocus()
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "workbench.panel.terminal",
            focusedClassName: "xterm-helper-textarea",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Disabled"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalCaptureDisabled, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_DisabledAllowsEmbeddedHostEditorFocus()
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "editor",
            focusedClassName: "monaco-editor",
            focusedControlType: "ControlType.Document",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Disabled"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.NonTerminal, decision.Risk);
        Assert.Equal(CopyShortcut.CtrlC, decision.Shortcut);
    }

    [Fact]
    public void EvaluateCopyPolicy_UsesEmbeddedRendererFocusMetadata()
    {
        var target = new ForegroundWindowInfo(
            WindowHandle,
            ProcessId: 7,
            ProcessName: "Code",
            FocusedAutomationId: "workbench.panel.terminal",
            FocusedClassName: "xterm-helper-textarea",
            FocusedControlType: "ControlType.Edit",
            FocusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Disabled"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedTerminal, decision.Risk);
    }

    [Fact]
    public void EvaluateCopyPolicy_EmbeddedHostWithoutFocusMetadataIsConservative()
    {
        var decision = Evaluate("Code", mode: "Smart");

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.SuspectedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalShortcutNotConfigured, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_AmbiguousEmbeddedFocusMetadataIsConservative()
    {
        var target = CreateWindow(
            "Code",
            focusedClassName: "Chrome_RenderWidgetHostHWND",
            focusedControlType: "ControlType.Document",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Smart"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.SuspectedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalShortcutNotConfigured, decision.Reason);
    }

    [Theory]
    [InlineData("Smart")]
    [InlineData("Compatible")]
    public void EvaluateCopyPolicy_EmbeddedTerminalRequiresExplicitMapping(string mode)
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "workbench.panel.terminal",
            focusedClassName: "xterm-helper-textarea",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings(mode));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalShortcutNotConfigured, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_CompatibleDoesNotGuessForSuspectedTerminal()
    {
        var decision = Evaluate("Code", mode: "Compatible");

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.SuspectedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalShortcutNotConfigured, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_DisabledRejectsExplicitMapping()
    {
        var decision = Evaluate(
            "CustomTerminal",
            mode: "Disabled",
            mappings: "CustomTerminal=Ctrl+C");

        Assert.False(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.TerminalCaptureDisabled, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_DisabledAllowsOrdinaryApplication()
    {
        var decision = Evaluate("notepad", mode: "Disabled");

        Assert.True(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.NonTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.OrdinaryApplication, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlC, decision.Shortcut);
        Assert.True(decision.RestoreClipboard);
    }

    [Fact]
    public void EvaluateCopyPolicy_SmartUsesSafeWindowsTerminalDefault()
    {
        var decision = Evaluate("WindowsTerminal", mode: "Smart");

        Assert.True(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.WindowsTerminalSafeDefault, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlShiftC, decision.Shortcut);
        Assert.False(decision.RestoreClipboard);
    }

    [Theory]
    [InlineData("conhost")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("OpenConsole")]
    public void EvaluateCopyPolicy_SmartRejectsTerminalWithoutMapping(string processName)
    {
        var decision = Evaluate(processName, mode: "Smart");

        Assert.False(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.TerminalShortcutNotConfigured, decision.Reason);
    }

    [Fact]
    public void EvaluateCopyPolicy_SmartUsesNormalizedExplicitMapping()
    {
        var decision = Evaluate(
            "CUSTOMTERMINAL",
            mode: "Smart",
            mappings: "customterminal.exe=Ctrl+Shift+C");

        Assert.True(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.ExplicitTerminalMapping, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlShiftC, decision.Shortcut);
        Assert.False(decision.RestoreClipboard);
    }

    [Fact]
    public void EvaluateCopyPolicy_CompatibleUsesCtrlShiftCForKnownTerminal()
    {
        var decision = Evaluate("pwsh", mode: "Compatible");

        Assert.True(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.CompatibleTerminalShortcut, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlShiftC, decision.Shortcut);
    }

    [Fact]
    public void TryCreateCopyRequest_PreservesAuditedDecision()
    {
        var settings = CreateSettings("Smart");
        var target = CreateWindow("WindowsTerminal");

        var allowed = TerminalDetector.TryCreateCopyRequest(target, settings, out var request, out var rejection);

        Assert.True(allowed);
        Assert.Null(rejection);
        Assert.NotNull(request);
        Assert.Equal(TerminalRiskKind.KnownTerminal, request.TerminalRisk);
        Assert.Equal(CopyDecisionReason.WindowsTerminalSafeDefault, request.DecisionReason);
    }

    [Fact]
    public void ShouldSuppressSelection_ChangesImmediatelyWithSettings()
    {
        var target = CreateWindow("pwsh");
        var settings = CreateSettings("Smart");

        Assert.False(TerminalDetector.ShouldSuppressSelection(target, settings));

        settings.TerminalCopyMode = "Disabled";

        Assert.True(TerminalDetector.ShouldSuppressSelection(target, settings));
    }

    [Fact]
    public void DecisionLogContext_ContainsOnlyDiagnosticMetadata()
    {
        const string secretTitle = "C:\\secret\\project - running private-command";
        const string secretText = "secret selected terminal text";
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "workbench.panel.terminal",
            focusedClassName: "xterm-helper-textarea",
            focusMetadataAvailable: true);
        var settings = CreateSettings("Disabled");
        var decision = TerminalDetector.EvaluateCopyPolicy(target, settings);

        var context = TerminalDetector.BuildDecisionLogContext(target, settings, decision);
        var json = Logger.Serialize(new LogEvent(
            DateTimeOffset.UtcNow,
            LogLevel.Debug,
            "TerminalDetector",
            "terminal.copy_decision",
            context));

        Assert.Contains("process_name", json, StringComparison.Ordinal);
        Assert.Contains("window_class", json, StringComparison.Ordinal);
        Assert.Contains("terminal_risk", json, StringComparison.Ordinal);
        Assert.DoesNotContain("window_title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command_line", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretTitle, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, json, StringComparison.Ordinal);
    }

    private static TerminalCopyDecision Evaluate(
        string processName,
        string mode,
        string mappings = "",
        string windowClass = "") =>
        TerminalDetector.EvaluateCopyPolicy(
            CreateWindow(processName, windowClass),
            CreateSettings(mode, mappings));

    private static ForegroundWindowInfo CreateWindow(
        string processName,
        string windowClass = "",
        string focusedAutomationId = "",
        string focusedClassName = "",
        string focusedControlType = "",
        bool focusMetadataAvailable = false) =>
        new(
            WindowHandle,
            7,
            processName,
            windowClass,
            focusedAutomationId,
            focusedClassName,
            focusedControlType,
            focusMetadataAvailable);

    private static AppSettings CreateSettings(string mode, string mappings = "") => new()
    {
        TerminalCopyMode = mode,
        TerminalCopyMappings = mappings
    };
}
