using System;
using System.ComponentModel.DataAnnotations;

namespace QuickTranslate.Database
{
    /// <summary>
    /// 翻译历史记录模型
    /// </summary>
    public class TranslationRecord
    {
        /// <summary>
        /// 主键
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 原文
        /// </summary>
        [Required]
        public string SourceText { get; set; } = string.Empty;

        /// <summary>
        /// 译文
        /// </summary>
        [Required]
        public string Translation { get; set; } = string.Empty;

        /// <summary>
        /// 源语言（自动检测或用户指定）
        /// </summary>
        public string SourceLanguage { get; set; } = string.Empty;

        /// <summary>
        /// 目标语言
        /// </summary>
        public string TargetLanguage { get; set; } = string.Empty;

        /// <summary>
        /// 翻译时间
        /// </summary>
        public DateTime TranslatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 来源应用（可选，暂时留空）
        /// </summary>
        public string SourceApp { get; set; } = string.Empty;
    }
}
