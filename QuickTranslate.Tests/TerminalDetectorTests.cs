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
    [InlineData("wezterm-gui")]
    [InlineData("alacritty")]
    [InlineData("mintty")]
    [InlineData("ConEmu64")]
    [InlineData("Hyper")]
    [InlineData("Tabby")]
    [InlineData("FluentTerminal")]
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
    [InlineData("mintty")]
    [InlineData("VirtualConsoleClass")]
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
    public void EvaluateCopyPolicy_AllowsEmbeddedWebViewFocusWithCtrlC()
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "root|RootWebArea|active-frame|c2e41810-c690-4441-b54c-dd35b8c4f917",
            focusedClassName: "messageInput_cKsPxg|vscode-dark vscode-using-screen-reader",
            focusedControlType: "ControlType.Edit|ControlType.Group|ControlType.Document",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Smart"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedWebView, decision.Risk);
        Assert.Equal(CopyDecisionReason.EmbeddedWebViewOrdinaryCopy, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlC, decision.Shortcut);
        Assert.True(decision.RestoreClipboard);
        Assert.Equal(CopyActionRisk.OrdinaryCopy, decision.ActionRisk);
    }

    [Fact]
    public void EvaluateCopyPolicy_DisabledRejectsEmbeddedWebViewFocus()
    {
        var target = CreateWindow(
            "Code",
            focusedClassName: "Chrome_RenderWidgetHostHWND",
            focusedControlType: "ControlType.Pane",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Disabled"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedWebView, decision.Risk);
        Assert.Equal(CopyDecisionReason.TerminalCaptureDisabled, decision.Reason);
    }

    [Theory]
    [InlineData("Smart")]
    [InlineData("Compatible")]
    public void EvaluateCopyPolicy_AllowsEmbeddedTerminalByDefault(string mode)
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "workbench.panel.terminal",
            focusedClassName: "xterm-helper-textarea",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings(mode));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedTerminal, decision.Risk);
        Assert.Equal(CopyDecisionReason.EmbeddedTerminalSafeDefault, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlShiftC, decision.Shortcut);
        Assert.True(decision.RestoreClipboard);
        Assert.Equal(CopyActionRisk.NonInterruptingTerminalCopy, decision.ActionRisk);
    }

    [Fact]
    public void EvaluateCopyPolicy_ExplicitMappingTakesPrecedenceOverEmbeddedDefault()
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "workbench.panel.terminal",
            focusedClassName: "xterm-helper-textarea",
            focusMetadataAvailable: true);

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Smart", "Code=Ctrl+Shift+C"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.ExplicitTerminalMapping, decision.Reason);
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
        Assert.True(decision.RestoreClipboard);
        Assert.Equal(CopyActionRisk.NonInterruptingTerminalCopy, decision.ActionRisk);
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
        Assert.True(decision.RestoreClipboard);
        Assert.Equal(CopyActionRisk.NonInterruptingTerminalCopy, decision.ActionRisk);
    }

    [Fact]
    public void EvaluateCopyPolicy_CompatibleUsesCtrlShiftCForKnownTerminal()
    {
        var decision = Evaluate("pwsh", mode: "Compatible");

        Assert.True(decision.IsAllowed);
        Assert.Equal(CopyDecisionReason.CompatibleTerminalShortcut, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlShiftC, decision.Shortcut);
        Assert.True(decision.RestoreClipboard);
    }

    [Fact]
    public void SelectionCapturePlanner_AllowsOrdinaryApplicationWithoutEvidence()
    {
        // 普通应用不经过终端选区证据门槛：无 UIA 证据也按普通 Ctrl+C 注入。
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("notepad"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.None,
            CreateIntent(SelectionGestureKind.HotKey));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(TerminalRiskKind.NonTerminal, plan.Request!.TerminalRisk);
        Assert.Equal(CopyShortcut.CtrlC, plan.Request.Shortcut);
        Assert.True(plan.Request.RestoreClipboard);
        Assert.Equal(CopyActionRisk.OrdinaryCopy, plan.Request.ActionRisk);
    }

    [Fact]
    public void ClipboardHelper_ProbesRecentAutoCopyOnlyForTerminalRisk()
    {
        Assert.True(ClipboardHelper.ShouldProbeRecentAutoCopy(TerminalRiskKind.KnownTerminal));
        Assert.True(ClipboardHelper.ShouldProbeRecentAutoCopy(TerminalRiskKind.EmbeddedTerminal));
        Assert.True(ClipboardHelper.ShouldProbeRecentAutoCopy(TerminalRiskKind.EmbeddedWebView));
        Assert.True(ClipboardHelper.ShouldProbeRecentAutoCopy(TerminalRiskKind.SuspectedTerminal));
        Assert.False(ClipboardHelper.ShouldProbeRecentAutoCopy(TerminalRiskKind.NonTerminal));
    }

    [Fact]
    public async Task ClipboardHelper_UnverifiedTerminalRequestReturnsWithoutInjection()
    {
        var request = new CopyRequest(
            ExpectedForegroundWindow: new IntPtr(42),
            Shortcut: CopyShortcut.CtrlC,
            RestoreClipboard: false,
            TerminalRisk: TerminalRiskKind.KnownTerminal,
            DecisionReason: CopyDecisionReason.ExplicitTerminalMapping,
            ActionRisk: CopyActionRisk.PotentialInterrupt,
            SelectionEvidence: SelectionEvidenceKind.GestureIntent);

        var result = await ClipboardHelper.GetSelectedTextAsync(request);

        Assert.Null(result);
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
    public void SelectionCapturePlanner_AllowsSafeTerminalWithoutUiaBounds()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("WindowsTerminal"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.GestureIntent,
            CreateIntent(SelectionGestureKind.Drag, endX: 20));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyActionRisk.NonInterruptingTerminalCopy, plan.Request!.ActionRisk);
    }

    [Fact]
    public void SelectionCapturePlanner_RejectsPotentialInterruptEvenWithUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("CustomTerminal"),
            CreateSettings("Smart", "CustomTerminal=Ctrl+C"),
            SelectionEvidenceKind.UiaTextSelectionBounds,
            CreateIntent(SelectionGestureKind.MultiClick));

        Assert.False(plan.IsAllowed);
        Assert.Equal(CopyActionRisk.PotentialInterrupt, plan.Decision.ActionRisk);
        Assert.Contains("中断", plan.RejectionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionCapturePlanner_RejectsUnverifiedCustomShortcut()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("CustomTerminal"),
            CreateSettings("Smart", "CustomTerminal=Alt+C"),
            SelectionEvidenceKind.GestureIntent,
            CreateIntent(SelectionGestureKind.Drag, endX: 20));

        Assert.False(plan.IsAllowed);
        Assert.Equal(CopyActionRisk.PotentialInterrupt, plan.Decision.ActionRisk);
    }

    [Fact]
    public void SelectionCapturePlanner_AllowsTerminalMultiClickWithoutUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("WindowsTerminal"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.GestureIntent,
            CreateIntent(SelectionGestureKind.MultiClick));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyShortcut.CtrlShiftC, plan.Request!.Shortcut);
        Assert.True(plan.Request.RestoreClipboard);
    }

    [Fact]
    public void SelectionCapturePlanner_AllowsTerminalHotKeyWithoutUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("WindowsTerminal"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.None,
            CreateIntent(SelectionGestureKind.HotKey));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyShortcut.CtrlShiftC, plan.Request!.Shortcut);
        Assert.True(plan.Request.RestoreClipboard);
    }

    [Fact]
    public void SelectionCapturePlanner_RejectsEmbeddedWebViewHotKeyWithoutUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateEmbeddedWebViewWindow(),
            CreateSettings("Smart"),
            SelectionEvidenceKind.None,
            CreateIntent(SelectionGestureKind.HotKey));

        Assert.False(plan.IsAllowed);
        Assert.Null(plan.Request);
    }

    [Fact]
    public void SelectionCapturePlanner_AllowsEmbeddedWebViewHotKeyWithUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateEmbeddedWebViewWindow(),
            CreateSettings("Smart"),
            SelectionEvidenceKind.UiaTextSelectionBounds,
            CreateIntent(SelectionGestureKind.HotKey));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyShortcut.CtrlC, plan.Request!.Shortcut);
        Assert.True(plan.Request.RestoreClipboard);
        Assert.Equal(CopyActionRisk.OrdinaryCopy, plan.Request.ActionRisk);
    }

    [Fact]
    public void SelectionCapturePlanner_AllowsEmbeddedWebViewDragWithGestureEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateEmbeddedWebViewWindow(),
            CreateSettings("Smart"),
            SelectionEvidenceKind.GestureIntent,
            CreateIntent(SelectionGestureKind.Drag, endX: 20));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyShortcut.CtrlC, plan.Request!.Shortcut);
    }

    private static ForegroundWindowInfo CreateEmbeddedWebViewWindow() =>
        CreateWindow(
            "Code",
            focusedAutomationId: "root|RootWebArea|active-frame|c2e41810-c690-4441-b54c-dd35b8c4f917",
            focusedClassName: "messageInput_cKsPxg|vscode-dark vscode-using-screen-reader",
            focusedControlType: "ControlType.Edit|ControlType.Group|ControlType.Document",
            focusMetadataAvailable: true);

    [Fact]
    public void SelectionCapturePlanner_AllowsTerminalMultiClickWithUiaEvidence()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("WindowsTerminal"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.UiaTextSelectionBounds,
            CreateIntent(SelectionGestureKind.MultiClick));

        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
    }

    [Fact]
    public void SelectionCapturePlanner_RejectsTerminalDragWithoutMeaningfulFinalDistance()
    {
        var plan = SelectionCapturePlanner.Create(
            CreateWindow("WindowsTerminal"),
            CreateSettings("Smart"),
            SelectionEvidenceKind.GestureIntent,
            CreateIntent(SelectionGestureKind.Drag, endX: 5));

        // 非中断快捷键不再受选区证据门槛约束：距离不足时注入 Ctrl+Shift+C
        // 最多复制不到内容，由"请先选中"提示兜底。
        Assert.True(plan.IsAllowed);
        Assert.NotNull(plan.Request);
        Assert.Equal(CopyShortcut.CtrlShiftC, plan.Request!.Shortcut);
    }

    [Fact]
    public void EvaluateCopyPolicy_RejectionWithoutShortcutHasNoActionRisk()
    {
        var decision = Evaluate("pwsh", mode: "Disabled");

        Assert.False(decision.IsAllowed);
        Assert.Equal(CopyActionRisk.NotApplicable, decision.ActionRisk);
    }

    [Fact]
    public void EvaluateCopyPolicy_GenericDocumentSurfaceTreatedAsEmbeddedWebView()
    {
        var decision = TerminalDetector.EvaluateCopyPolicy(
            CreateWindow(
                "Code",
                focusedClassName: "Chrome_RenderWidgetHostHWND",
                focusedControlType: "ControlType.Document",
                focusMetadataAvailable: true),
            CreateSettings("Smart"));

        Assert.True(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.EmbeddedWebView, decision.Risk);
        Assert.Equal(CopyDecisionReason.EmbeddedWebViewOrdinaryCopy, decision.Reason);
        Assert.Equal(CopyShortcut.CtrlC, decision.Shortcut);
        Assert.True(decision.RestoreClipboard);
    }

    [Fact]
    public void EvaluateCopyPolicy_MismatchedFocusProcessIsConservative()
    {
        var target = CreateWindow(
            "Code",
            focusedAutomationId: "editor",
            focusedClassName: "monaco-editor",
            focusedControlType: "ControlType.Document",
            focusMetadataAvailable: true) with
        {
            FocusedProcessId = 99
        };

        var decision = TerminalDetector.EvaluateCopyPolicy(target, CreateSettings("Smart"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(TerminalRiskKind.SuspectedTerminal, decision.Risk);
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
            FocusedProcessId: focusMetadataAvailable ? 7 : 0,
            FocusMetadataAvailable: focusMetadataAvailable);

    private static AppSettings CreateSettings(string mode, string mappings = "") => new()
    {
        TerminalCopyMode = mode,
        TerminalCopyMappings = mappings
    };

    private static SelectionIntent CreateIntent(SelectionGestureKind gestureKind, double endX = 0) =>
        new(
            gestureKind,
            new System.Windows.Point(0, 0),
            new System.Windows.Point(endX, 0),
            DateTimeOffset.UtcNow);
}
