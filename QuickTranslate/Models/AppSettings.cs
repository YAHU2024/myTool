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
    }
}
