using System.Windows;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

public enum SelectionGestureKind
{
    Drag,
    MultiClick,
    HotKey
}

internal sealed record SelectionIntent(
    SelectionGestureKind GestureKind,
    Point StartPoint,
    Point EndPoint,
    DateTimeOffset OccurredAt)
{
    internal bool HasMeaningfulDrag =>
        GestureKind == SelectionGestureKind.Drag &&
        (EndPoint - StartPoint).LengthSquared > 100;
}

internal enum SelectionEvidenceKind
{
    None,
    GestureIntent,
    UiaTextSelectionBounds
}

internal enum CopyActionRisk
{
    OrdinaryCopy,
    NonInterruptingTerminalCopy,
    PotentialInterrupt
}

internal sealed record SelectionCapturePlan(
    bool IsAllowed,
    CopyRequest? Request,
    TerminalCopyDecision Decision,
    string? RejectionMessage);

internal static class SelectionCapturePlanner
{
    internal static SelectionCapturePlan Create(
        ForegroundWindowInfo? target,
        AppSettings settings,
        SelectionEvidenceKind evidence,
        SelectionGestureKind gestureKind)
    {
        var decision = TerminalDetector.EvaluateCopyPolicy(target, settings);
        if (!decision.IsAllowed || target == null || decision.Shortcut == null)
            return new(false, null, decision, decision.RejectionMessage);

        if (decision.ActionRisk == CopyActionRisk.PotentialInterrupt)
        {
            return new(
                false,
                null,
                decision,
                "该终端复制键可能中断正在运行的命令，已取消取词");
        }

        if (decision.Risk != TerminalRiskKind.NonTerminal &&
            evidence != SelectionEvidenceKind.UiaTextSelectionBounds &&
            gestureKind != SelectionGestureKind.Drag)
        {
            return new(
                false,
                null,
                decision,
                gestureKind == SelectionGestureKind.HotKey
                    ? "无法确认终端文本选区，已取消快捷键取词"
                    : "无法确认终端多击产生了文本选区，已取消取词");
        }

        var request = new CopyRequest(
            target.Handle,
            decision.Shortcut,
            decision.RestoreClipboard,
            decision.Risk,
            decision.Reason,
            decision.ActionRisk,
            evidence);

        return new(true, request, decision, null);
    }
}
