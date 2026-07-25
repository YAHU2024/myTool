namespace QuickTranslate.Models
{
    /// <summary>
    /// 翻译触发方式：划词圆点与全局快捷键的组合模式。
    /// </summary>
    public enum TranslationTriggerMode
    {
        /// <summary>同时启用划词圆点和快捷键。</summary>
        Both = 0,

        /// <summary>仅划词圆点翻译。</summary>
        SelectionOnly = 1,

        /// <summary>仅快捷键翻译。</summary>
        HotKeyOnly = 2,

        /// <summary>全部关闭（暂停）。</summary>
        Off = 3
    }

    /// <summary>
    /// 翻译触发模式的派生规则、规范化与暂停/恢复纯逻辑。
    /// </summary>
    public static class TranslationTriggerModes
    {
        public static bool IsDefined(TranslationTriggerMode mode) =>
            mode is TranslationTriggerMode.Both
                or TranslationTriggerMode.SelectionOnly
                or TranslationTriggerMode.HotKeyOnly
                or TranslationTriggerMode.Off;

        public static TranslationTriggerMode Normalize(TranslationTriggerMode mode) =>
            IsDefined(mode) ? mode : TranslationTriggerMode.Both;

        /// <summary>
        /// 规范化可恢复的活动模式；Off/非法值回退为 Both。
        /// </summary>
        public static TranslationTriggerMode NormalizeActive(TranslationTriggerMode mode)
        {
            mode = Normalize(mode);
            return mode == TranslationTriggerMode.Off
                ? TranslationTriggerMode.Both
                : mode;
        }

        public static bool CanTriggerSelection(TranslationTriggerMode mode) =>
            Normalize(mode) is TranslationTriggerMode.Both or TranslationTriggerMode.SelectionOnly;

        public static bool CanTriggerHotKey(TranslationTriggerMode mode) =>
            Normalize(mode) is TranslationTriggerMode.Both or TranslationTriggerMode.HotKeyOnly;

        public static bool IsPaused(TranslationTriggerMode mode) =>
            Normalize(mode) == TranslationTriggerMode.Off;

        public static string GetDisplayName(TranslationTriggerMode mode) => Normalize(mode) switch
        {
            TranslationTriggerMode.Both => "同时启用划词圆点和快捷键",
            TranslationTriggerMode.SelectionOnly => "仅划词圆点翻译",
            TranslationTriggerMode.HotKeyOnly => "仅快捷键翻译",
            TranslationTriggerMode.Off => "全部关闭",
            _ => "同时启用划词圆点和快捷键"
        };

        public static string GetTrayStatusText(TranslationTriggerMode mode) => Normalize(mode) switch
        {
            TranslationTriggerMode.Both => "已启用（划词+快捷键）",
            TranslationTriggerMode.SelectionOnly => "已启用（仅划词）",
            TranslationTriggerMode.HotKeyOnly => "已启用（仅快捷键）",
            TranslationTriggerMode.Off => "翻译已暂停",
            _ => "已启用（划词+快捷键）"
        };

        /// <summary>
        /// 暂停：切到 Off，并在当前为活动模式时记住 LastActive。
        /// 重复暂停不破坏已记录的 LastActive。
        /// </summary>
        public static (TranslationTriggerMode Mode, TranslationTriggerMode LastActive) Pause(
            TranslationTriggerMode current,
            TranslationTriggerMode lastActive)
        {
            lastActive = NormalizeActive(lastActive);
            current = Normalize(current);
            if (current != TranslationTriggerMode.Off)
                lastActive = current;
            return (TranslationTriggerMode.Off, lastActive);
        }

        /// <summary>
        /// 恢复：回到 LastActive；异常/Off 回退 Both。
        /// </summary>
        public static TranslationTriggerMode Resume(TranslationTriggerMode lastActive) =>
            NormalizeActive(lastActive);

        /// <summary>
        /// 设置页保存活动模式时同步 LastActive；选 Off 时不改 LastActive。
        /// </summary>
        public static TranslationTriggerMode RememberActiveIfNeeded(
            TranslationTriggerMode selectedMode,
            TranslationTriggerMode lastActive)
        {
            selectedMode = Normalize(selectedMode);
            lastActive = NormalizeActive(lastActive);
            return selectedMode == TranslationTriggerMode.Off
                ? lastActive
                : selectedMode;
        }
    }
}
