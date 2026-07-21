using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 内容类型枚举
    /// </summary>
    public enum ContentType
    {
        /// <summary>普通文本 → 翻译</summary>
        Translation,
        /// <summary>代码或终端命令 → 解析</summary>
        Code,
        /// <summary>纯英文专有名词/技术术语 → 解释</summary>
        Term,
        /// <summary>解析标签触发 → 深度解析</summary>
        Analysis
    }

    /// <summary>
    /// 本地内容类型检测器（零延迟正则特征匹配）
    /// 设计原则：宁可 Uncertain(Translation) 也不误判
    /// </summary>
    public static class ContentTypeDetector
    {
        // ─── 代码/命令特征 ───

        /// <summary>以 shell 提示符开头：$ 、> 、#</summary>
        private static readonly Regex ShellPromptPrefix = new(
            @"^[\$>#]\s+\S", RegexOptions.Compiled);

        /// <summary>包含管道、逻辑运算符、重定向：| 、&& 、|| 、>> 、2>&1</summary>
        private static readonly Regex ShellOperators = new(
            @"\s(\|\||&&|>>?|<)\s|2>&1", RegexOptions.Compiled);

        /// <summary>包含常见命令行参数格式：--flag 、-f 、-n 10</summary>
        private static readonly Regex CliFlags = new(
            @"(^|\s)--?[a-zA-Z][\w-]*(\s|=|$)", RegexOptions.Compiled);

        /// <summary>常见命令前缀（行首）</summary>
        private static readonly Regex KnownCommands = new(
            @"^(git|npm|npx|yarn|pnpm|pip|pip3|docker|kubectl|cargo|go|dotnet|msbuild|cd|ls|dir|cat|grep|find|curl|wget|ssh|scp|make|cmake|python|python3|node|java|javac|gcc|g\+\+|rustc|apt|brew|choco|winget|sudo|chmod|chown|mkdir|rm|cp|mv|tar|zip|unzip)\s",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>包含文件路径特征</summary>
        private static readonly Regex FilePathPattern = new(
            @"(/usr/|/etc/|/home/|/var/|C:\\|D:\\|\.\w{1,4}\b)", RegexOptions.Compiled);

        /// <summary>URL 特征：http/https 开头</summary>
        private static readonly Regex UrlPattern = new(
            @"^https?://\S+", RegexOptions.Compiled);

        /// <summary>代码特征：花括号、分号结尾、函数调用、赋值</summary>
        private static readonly Regex CodeSyntax = new(
            @"(\{[^}]*\}|;\s*$|=>|\w+\([^)]*\)\s*[{;]|import\s+\w+|from\s+\w+\s+import|#include)",
            RegexOptions.Compiled);

        // ─── 纯英文术语特征 ───

        /// <summary>纯英文（允许空格、连字符、点号、数字），无中文</summary>
        private static readonly Regex PureEnglish = new(
            @"^[a-zA-Z0-9\s\.\-+#/]+$", RegexOptions.Compiled);

        /// <summary>
        /// 检测输入文本的内容类型
        /// </summary>
        public static ContentType Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ContentType.Translation;

            var trimmed = text.Trim();

            // ─── URL 检测（优先） ───
            if (UrlPattern.IsMatch(trimmed))
                return ContentType.Code;

            // ─── 代码/命令检测（高置信度才触发） ───
            if (IsCodeOrCommand(trimmed))
                return ContentType.Code;

            // ─── 纯英文术语检测 ───
            if (IsEnglishTerm(trimmed))
                return ContentType.Term;

            // ─── 默认：翻译 ───
            return ContentType.Translation;
        }

        /// <summary>
        /// 判断是否为代码或终端命令
        /// 需要满足至少 2 个特征才判定（避免误判）
        /// </summary>
        private static bool IsCodeOrCommand(string text)
        {
            int score = 0;

            if (ShellPromptPrefix.IsMatch(text)) score += 2;  // 强信号
            if (KnownCommands.IsMatch(text)) score += 2;       // 强信号
            if (ShellOperators.IsMatch(text)) score++;
            if (CliFlags.IsMatch(text)) score++;
            if (CodeSyntax.IsMatch(text)) score++;

            // 短文本（单行）只需 2 分；多行文本需要 3 分
            int threshold = text.Contains('\n') ? 3 : 2;
            return score >= threshold;
        }

        /// <summary>
        /// 判断是否为纯英文专有名词/技术术语
        /// 条件：全英文 + 短（≤50字符） + 非完整句子
        /// </summary>
        private static bool IsEnglishTerm(string text)
        {
            // 必须是纯英文（无中文、无其他非ASCII字符）
            if (!PureEnglish.IsMatch(text))
                return false;

            // 长度限制：术语通常较短
            if (text.Length > 50)
                return false;

            // 排除完整句子（包含句号、问号、感叹号 + 空格数量多）
            if (text.Contains('.') && text.Contains(' ') && text.Length > 30)
                return false;
            if (text.Contains('?') || text.Contains('!'))
                return false;

            // 排除纯数字
            if (double.TryParse(text, out _))
                return false;

            // 至少包含一个字母
            if (!text.Any(char.IsLetter))
                return false;

            return true;
        }
    }
}
