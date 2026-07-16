using System.Collections.Generic;

namespace QuickTranslate.Models
{
    /// <summary>
    /// 应用配置模型
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// OpenAI 兼容接口的 Base URL
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>
        /// API Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; set; } = "gpt-4o-mini";

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
    }
}
