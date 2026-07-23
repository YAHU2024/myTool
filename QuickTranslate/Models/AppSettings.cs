using System.Collections.Generic;

namespace QuickTranslate.Models
{
    /// <summary>
    /// 已保存的配置组合（模型 + URL + Key）
    /// </summary>
    public class SavedConfig
    {
        /// <summary>
        /// 显示名称（用于下拉框展示，如 "glm-4.7-flash @ 智谱"）
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// API Base URL
        /// </summary>
        public string ApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// API Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 应用配置模型
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// OpenAI 兼容接口的 Base URL
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";

        /// <summary>
        /// API Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; set; } = "glm-4.7-flash";

        /// <summary>
        /// 目标翻译语言
        /// </summary>
        public string TargetLanguage { get; set; } = "简体中文";

        /// <summary>
        /// 备选语言（源语言与目标语言相同时翻译为备选语言）
        /// </summary>
        public string FallbackLanguage { get; set; } = "English";

        /// <summary>
        /// 根据目标语言获取推荐的备选语言
        /// </summary>
        public static string GetRecommendedFallback(string targetLanguage)
        {
            return targetLanguage switch
            {
                "简体中文" or "繁体中文" => "English",
                "English" => "简体中文",
                "日本語" or "한국어" => "简体中文",
                _ => "English"
            };
        }

        /// <summary>
        /// 支持的语言列表
        /// </summary>
        public List<string> SupportedLanguages { get; set; } = new()
        {
            "简体中文",
            "繁体中文",
            "English",
            "日本語",
            "한국어",
            "Français",
            "Deutsch",
            "Español",
            "Русский",
            "Português",
            "Italiano",
            "العربية",
            "Tiếng Việt",
            "ไทย"
        };

        /// <summary>
        /// 是否启用翻译功能
        /// </summary>
        public bool TranslationEnabled { get; set; } = true;

        /// <summary>
        /// 是否开机自启
        /// </summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>
        /// 已保存的配置组合列表（最近使用）
        /// </summary>
        public List<SavedConfig> SavedConfigs { get; set; } = new();

        // ==================== 第四期：体验优化 ====================

        /// <summary>
        /// 是否启用快捷键
        /// </summary>
        public bool HotKeyEnabled { get; set; } = true;

        /// <summary>
        /// 快捷键虚拟键码（默认 Q = 0x51）
        /// </summary>
        public byte HotKeyVK { get; set; } = 0x51;

        /// <summary>
        /// 快捷键是否需要 Alt 修饰键
        /// </summary>
        public bool HotKeyRequireAlt { get; set; } = true;

        /// <summary>
        /// 快捷键是否需要 Ctrl 修饰键
        /// </summary>
        public bool HotKeyRequireCtrl { get; set; } = false;

        /// <summary>
        /// 快捷键是否需要 Shift 修饰键
        /// </summary>
        public bool HotKeyRequireShift { get; set; } = false;

        /// <summary>
        /// 是否启用语言自动检测（根据源语言自动决定翻译方向）
        /// </summary>
        public bool AutoDetectLanguage { get; set; } = true;

        /// <summary>
        /// 是否启用智能内容识别（代码/命令→解析，专有名词→解释，其余→翻译）
        /// </summary>
        public bool SmartContentType { get; set; } = false;

        /// <summary>
        /// 自定义翻译提示词（留空使用默认，支持 {targetLang} 占位符）
        /// </summary>
        public string CustomTranslationPrompt { get; set; } = string.Empty;

        /// <summary>
        /// 自定义深度解析提示词（留空使用解析预设，支持 {targetLang} 占位符）
        /// </summary>
        public string CustomAnalysisPrompt { get; set; } = string.Empty;

        /// <summary>
        /// 是否在浏览器中启用翻译（关闭后避免与浏览器翻译插件冲突）
        /// </summary>
        public bool EnableInBrowser { get; set; } = true;

        /// <summary>
        /// 解析预设标识（general/learner/literary/business）
        /// </summary>
        public string AnalysisPreset { get; set; } = "general";

        /// <summary>
        /// 当前生效的解析方案 ID（builtin:* 或 custom:*）。
        /// </summary>
        public string SelectedAnalysisPromptId { get; set; } = "builtin:general";

        /// <summary>
        /// 用户保存的解析方案。
        /// </summary>
        public List<AnalysisPromptProfile> AnalysisPromptProfiles { get; set; } = new();

        /// <summary>
        /// 用户自定义的浏览器进程名（逗号分隔，补充内置列表）
        /// </summary>
        public string CustomBrowserProcesses { get; set; } = string.Empty;

        /// <summary>
        /// Terminal text capture policy: Smart, Compatible, or Disabled.
        /// </summary>
        public string TerminalCopyMode { get; set; } = "Smart";

        /// <summary>
        /// Per-process terminal copy shortcuts, for example:
        /// WindowsTerminal=Ctrl+Shift+C;conhost=Ctrl+C
        /// </summary>
        public string TerminalCopyMappings { get; set; } = string.Empty;

        // ==================== 日志配置 ====================

        /// <summary>
        /// 日志级别（Debug/Info/Warn/Error/Fatal）
        /// </summary>
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// 日志保留天数
        /// </summary>
        public int LogRetentionDays { get; set; } = 7;

        /// <summary>
        /// Maximum total size of managed log files in bytes.
        /// </summary>
        public long LogMaxTotalBytes { get; set; } = 50 * 1024 * 1024;
    }
}
