namespace QuickTranslate.Core;

/// <summary>
/// 判定取词时剪贴板中的内容是否来自目标应用的“选中即复制”行为
/// （如 Claude Code 的 OSC 52 分块复制、编辑器的 copyOnSelection）。
/// 纯逻辑判定，便于单元测试；基线由 SelectionDetector 的鼠标钩子采集。
/// </summary>
internal static class RecentSelectionCopyEvaluator
{
    /// <summary>热键取词距鼠标抬起的最长时间，超过则视为过期不再复用。</summary>
    internal const long MouseUpFreshnessMs = 10_000;

    /// <summary>整个选区动作（按下到当前）的最长时间，防止陈旧基线误命中。</summary>
    internal const long MouseDownFreshnessMs = 30_000;

    internal static bool IsAutoCopySuspected(
        long sequenceAtMouseDown,
        long mouseDownTick,
        long mouseUpTick,
        long currentSequence,
        long nowTick)
    {
        if (sequenceAtMouseDown < 0)
            return false;
        if (currentSequence == sequenceAtMouseDown)
            return false;
        if (mouseDownTick < 0 || mouseUpTick < mouseDownTick)
            return false;
        if (nowTick - mouseDownTick > MouseDownFreshnessMs)
            return false;
        if (nowTick - mouseUpTick > MouseUpFreshnessMs)
            return false;
        return true;
    }
}
