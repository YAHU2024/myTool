using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslate.Helpers;

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

    internal sealed record DetectionResult(
        ContentType ContentType,
        int Score,
        int Threshold,
        IReadOnlyList<string> MatchedFeatures,
        int CharacterCount,
        TimeSpan Elapsed);

    /// <summary>
    /// 本地内容类型检测器（零延迟正则特征匹配）
    /// 设计原则：宁可 Uncertain(Translation) 也不误判
    /// </summary>
    public static class ContentTypeDetector
    {
        private const int MaxFullJsonParseCharacters = 256 * 1024;
        private const int JsonSampleCharacters = 4 * 1024;

        private static readonly Regex ShellPromptPrefix = new(
            @"^[\$>#]\s+\S", RegexOptions.Compiled);

        private static readonly Regex ShellOperators = new(
            @"(\|\||&&|>>?|2>&1)", RegexOptions.Compiled);

        private static readonly Regex CliFlags = new(
            @"(^|\s)--?[a-zA-Z][\w-]*(\s|=|$)", RegexOptions.Compiled);

        private static readonly Regex KnownCommands = new(
            @"^(git|npm|npx|yarn|pnpm|pip|pip3|docker|kubectl|cargo|go|dotnet|msbuild|cd|ls|dir|cat|grep|find|curl|wget|ssh|scp|make|cmake|python|python3|node|java|javac|gcc|g\+\+|rustc|apt|brew|choco|winget|sudo|chmod|chown|mkdir|rm|cp|mv|tar|zip|unzip)\s",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FilePathPattern = new(
            @"(/usr/|/etc/|/home/|/var/|C:\\|D:\\|\.\w{1,4}\b)", RegexOptions.Compiled);

        private static readonly Regex UrlPattern = new(
            @"^https?://\S+", RegexOptions.Compiled);

        private static readonly Regex CodeSyntax = new(
            @"(;\s*$|=>|\b(import|from)\s+[A-Za-z_]|#include|\b(class|interface|namespace|public|private|protected|static|async|await|def|func|function|fn|const|let|var|using|SELECT|CREATE|INSERT|UPDATE|WHERE)\b|\b[A-Za-z_$][\w$]*\s*:=|\b[A-Za-z_$][\w$]*\s*=\s*[^=])",
            RegexOptions.Compiled);

        private static readonly Regex FencedCodeBlock = new(
            @"(?s)^\s*```(?:[A-Za-z0-9_+#.-]+)?\s*\r?\n.+?\r?\n\s*```\s*$", RegexOptions.Compiled);

        private static readonly Regex DeclarationSyntax = new(
            @"(?m)^\s*(?:(?:public|private|protected|internal|static|async|export|const|let|var|class|interface|struct|enum|def|func|function|fn)\b|(?:if|for|while|switch|try|catch)\s*\([^)]*\)\s*\{?)", RegexOptions.Compiled);

        private static readonly Regex CodeComment = new(
            @"(?m)^\s*(?://|/\*|\*|#(?!include)|--|REM\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SqlBlock = new(
            @"(?is)^\s*(?:SELECT\b.+\bFROM\b|INSERT\s+INTO\b|UPDATE\b.+\bSET\b|DELETE\s+FROM\b|CREATE\s+(?:TABLE|VIEW|INDEX)\b)", RegexOptions.Compiled);

        private static readonly Regex PowerShellCommand = new(
            @"(?im)^\s*(?:Get|Set|New|Remove|Start|Stop|Invoke|Test|Select|Where|ForEach|Write|Import|Export)-[A-Za-z][\w-]*\b", RegexOptions.Compiled);

        private static readonly Regex YamlKeyValueLine = new(
            @"(?m)^[ \t]*[A-Za-z_][\w.-]*:[ \t]+\S.*$", RegexOptions.Compiled);

        private static readonly Regex YamlContainerKeyLine = new(
            @"(?m)^[ \t]*[A-Za-z_][\w.-]*:[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex YamlStructureLine = new(
            @"(?m)^(?:[ \t]{2,}\S|[ \t]*-[ \t]+\S)", RegexOptions.Compiled);

        private static readonly Regex SectionHeader = new(
            @"(?m)^\s*\[[A-Za-z0-9_.-]+\]\s*$", RegexOptions.Compiled);

        private static readonly Regex AssignmentLine = new(
            @"(?m)^\s*[A-Za-z_][\w.-]*\s*=\s*.+$", RegexOptions.Compiled);

        private static readonly Regex EnvironmentAssignmentLine = new(
            @"(?m)^\s*[A-Z_][A-Z0-9_]*\s*=\s*.*$", RegexOptions.Compiled);

        private static readonly Regex XmlDeclaration = new(
            @"^\s*<\?xml\s+[^?]+\?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex XmlPairedTag = new(
            @"(?s)^\s*<([A-Za-z_][\w:.-]*)\b[^>]*>.*</\1>\s*$", RegexOptions.Compiled);

        private static readonly Regex XmlSelfClosingTag = new(
            @"^\s*<[A-Za-z_][\w:.-]*\b[^>]*/>\s*$", RegexOptions.Compiled);

        private static readonly Regex PureEnglish = new(
            @"^[a-zA-Z0-9\s\.\-+#/]+$", RegexOptions.Compiled);

        private static readonly Regex MixedTechnicalTerm = new(
            @"^(?<english>[A-Za-z0-9.+#/\-]+(?:\s+[A-Za-z0-9.+#/\-]+){0,3})\s+(?<modifier>[\u4e00-\u9fff]{1,5})$",
            RegexOptions.Compiled);

        private static readonly HashSet<string> AmbiguousBareCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "cat", "find", "make", "go"
        };

        private static readonly HashSet<string> ChineseTermModifiers = new(StringComparer.Ordinal)
        {
            "集群", "用法", "配置", "组件", "框架", "原理", "区别", "教程"
        };

        private static readonly HashSet<string> KnownTechnicalTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "Kubernetes", "React", "Docker", "Git", "Linux", "Windows", "API", "SDK", "SQL",
            "JSON", "YAML", "TOML", "XML", "HTTP", "HTML", "CSS", "JavaScript", "TypeScript",
            "Python", "PowerShell", "WPF", "EF", "Core", "OpenAI", "Claude", "Codex", ".NET"
        };

        public static ContentType Detect(string text)
        {
            var result = DetectDetailed(text);
            Logger.Debug("ContentTypeDetector", FormatDiagnostic(result));
            return result.ContentType;
        }

        internal static DetectionResult DetectDetailed(string text)
        {
            var stopwatch = Stopwatch.StartNew();
            var features = new List<string>();
            var characterCount = text?.Length ?? 0;

            DetectionResult Finish(ContentType contentType, int score, int threshold)
            {
                stopwatch.Stop();
                return new DetectionResult(
                    contentType,
                    score,
                    threshold,
                    features.AsReadOnly(),
                    characterCount,
                    stopwatch.Elapsed);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                features.Add("empty");
                return Finish(ContentType.Translation, 0, 0);
            }

            var trimmed = text.Trim();
            var threshold = trimmed.Contains('\n') ? 3 : 2;

            if (AmbiguousBareCommands.Contains(trimmed))
            {
                features.Add("ambiguous-bare-word");
                return Finish(ContentType.Translation, 0, threshold);
            }

            if (FencedCodeBlock.IsMatch(trimmed))
            {
                features.Add("fenced-code");
                return Finish(ContentType.Code, threshold, threshold);
            }

            if (UrlPattern.IsMatch(trimmed))
            {
                features.Add("url");
                return Finish(ContentType.Code, threshold, threshold);
            }

            if (IsCodeOrCommand(trimmed, threshold, features, out var score))
                return Finish(ContentType.Code, score, threshold);

            if (IsMultilineTechnicalDefinition(trimmed))
            {
                features.Add("technical-definition");
                return Finish(ContentType.Term, score, threshold);
            }

            if (IsMixedEnglishTerm(trimmed))
            {
                features.Add("mixed-technical-term");
                return Finish(ContentType.Term, score, threshold);
            }

            if (IsEnglishTerm(trimmed))
            {
                features.Add("english-term");
                return Finish(ContentType.Term, score, threshold);
            }

            return Finish(ContentType.Translation, score, threshold);
        }

        internal static string FormatDiagnostic(DetectionResult result)
        {
            var features = result.MatchedFeatures.Count == 0
                ? "none"
                : string.Join(',', result.MatchedFeatures);
            return $"type={result.ContentType}, score={result.Score}, threshold={result.Threshold}, " +
                   $"chars={result.CharacterCount}, elapsedMs={result.Elapsed.TotalMilliseconds:F3}, features=[{features}]";
        }

        private static bool IsCodeOrCommand(
            string text,
            int threshold,
            List<string> features,
            out int score)
        {
            score = 0;
            var accumulatedScore = 0;
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            var jsonMatch = GetJsonMatch(text);
            if (jsonMatch != JsonMatch.None)
            {
                features.Add(jsonMatch == JsonMatch.Valid ? "json-valid" : "json-large-candidate");
                score = threshold;
                return true;
            }

            if (LooksLikeConfiguration(text, features))
            {
                score = threshold;
                return true;
            }

            if (SqlBlock.IsMatch(text))
            {
                features.Add("sql-block");
                score = threshold;
                return true;
            }

            AddScore(PowerShellCommand.IsMatch(text), 2, "powershell-command");
            AddScore(DeclarationSyntax.IsMatch(text), 2, "declaration");
            AddScore(CodeComment.IsMatch(text), 1, "code-comment");
            AddScore(text.Contains('{') && text.Contains('}'), 2, "balanced-braces");
            AddScore(text.Contains('\n') && lines.Length >= 2 && lines.Any(l => l.TrimEnd().EndsWith(';')), 1, "multiline-semicolon");
            AddScore(ShellPromptPrefix.IsMatch(text), 2, "shell-prompt");
            AddScore(KnownCommands.IsMatch(text), 2, "known-command");
            AddScore(ShellOperators.IsMatch(text), 1, "shell-operator");
            AddScore(CliFlags.IsMatch(text), 1, "cli-flag");
            AddScore(CodeSyntax.IsMatch(text), 1, "code-syntax");
            AddScore(FilePathPattern.IsMatch(text), 1, "file-path");

            score = accumulatedScore;
            return score >= threshold;

            void AddScore(bool matched, int points, string feature)
            {
                if (!matched)
                    return;

                accumulatedScore += points;
                features.Add(feature);
            }
        }

        private static bool LooksLikeConfiguration(string text, List<string> features)
        {
            var yamlCount = YamlKeyValueLine.Matches(text).Count;
            var hasYamlStructure = YamlStructureLine.IsMatch(text);
            if (yamlCount >= 2 ||
                (yamlCount >= 1 && hasYamlStructure) ||
                (YamlContainerKeyLine.IsMatch(text) && hasYamlStructure))
            {
                features.Add("yaml-structure");
                return true;
            }

            if (SectionHeader.IsMatch(text) && AssignmentLine.IsMatch(text))
            {
                features.Add("section-assignments");
                return true;
            }

            if (XmlDeclaration.IsMatch(text) || XmlPairedTag.IsMatch(text) || XmlSelfClosingTag.IsMatch(text))
            {
                features.Add("xml-structure");
                return true;
            }

            var environmentMatches = EnvironmentAssignmentLine.Matches(text);
            if (environmentMatches.Count >= 2 ||
                (environmentMatches.Count == 1 && HasEnvironmentValueSignal(environmentMatches[0].Value)))
            {
                features.Add("environment-assignments");
                return true;
            }

            return false;
        }

        private static bool HasEnvironmentValueSignal(string assignment)
        {
            var separatorIndex = assignment.IndexOf('=');
            if (separatorIndex < 0)
                return false;

            var value = assignment[(separatorIndex + 1)..].Trim();
            return value.StartsWith('"') || value.StartsWith('\'') || value.Contains("${", StringComparison.Ordinal);
        }

        private static JsonMatch GetJsonMatch(string text)
        {
            var trimmed = text.Trim();
            var isObject = trimmed.StartsWith('{') && trimmed.EndsWith('}');
            var isArray = trimmed.StartsWith('[') && trimmed.EndsWith(']');
            if (!isObject && !isArray)
                return JsonMatch.None;

            if (trimmed.Length <= MaxFullJsonParseCharacters)
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        ? JsonMatch.Valid
                        : JsonMatch.None;
                }
                catch (JsonException)
                {
                    return JsonMatch.None;
                }
            }

            var span = trimmed.AsSpan();
            var prefixLength = Math.Min(JsonSampleCharacters, span.Length);
            var suffixStart = Math.Max(0, span.Length - JsonSampleCharacters);
            var prefix = span[..prefixLength];
            var suffix = span[suffixStart..];

            if (isObject && (ContainsJsonObjectMember(prefix) || ContainsJsonObjectMember(suffix)))
                return JsonMatch.LargeCandidate;

            if (isArray && HasJsonArrayValue(prefix))
                return JsonMatch.LargeCandidate;

            return JsonMatch.None;
        }

        private static bool ContainsJsonObjectMember(ReadOnlySpan<char> sample)
        {
            for (var i = 0; i < sample.Length; i++)
            {
                if (sample[i] != '"')
                    continue;

                var escaped = false;
                for (var j = i + 1; j < sample.Length; j++)
                {
                    if (sample[j] == '\\' && !escaped)
                    {
                        escaped = true;
                        continue;
                    }

                    if (sample[j] == '"' && !escaped)
                    {
                        var next = j + 1;
                        while (next < sample.Length && char.IsWhiteSpace(sample[next]))
                            next++;
                        if (next < sample.Length && sample[next] == ':')
                            return true;
                        break;
                    }

                    escaped = false;
                }
            }

            return false;
        }

        private static bool HasJsonArrayValue(ReadOnlySpan<char> prefix)
        {
            var index = 1;
            while (index < prefix.Length && char.IsWhiteSpace(prefix[index]))
                index++;
            if (index >= prefix.Length)
                return false;

            var first = prefix[index];
            return first is '"' or '{' or '[' or '-' or 't' or 'f' or 'n' || char.IsDigit(first);
        }

        private static bool IsMixedEnglishTerm(string text)
        {
            var match = MixedTechnicalTerm.Match(text);
            if (!match.Success)
                return false;

            var english = match.Groups["english"].Value;
            var modifier = match.Groups["modifier"].Value;
            return ChineseTermModifiers.Contains(modifier) && HasTechnicalShape(english);
        }

        private static bool IsMultilineTechnicalDefinition(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
                return false;

            var heading = lines[0].Trim();
            if (heading.Length == 0 || heading.Length > 50 || !IsEnglishTerm(heading) || !HasTechnicalShape(heading))
                return false;

            var definition = string.Join('\n', lines.Skip(1)).TrimStart();
            return definition.StartsWith("是", StringComparison.Ordinal) ||
                   definition.StartsWith("指", StringComparison.Ordinal) ||
                   definition.StartsWith("用于", StringComparison.Ordinal) ||
                   definition.StartsWith("表示", StringComparison.Ordinal);
        }

        private static bool HasTechnicalShape(string english)
        {
            var tokens = english.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(KnownTechnicalTerms.Contains))
                return true;

            return tokens.Any(token =>
                (token.Length >= 2 && token.All(char.IsUpper)) ||
                (token.Skip(1).Any(char.IsUpper) && token.Any(char.IsLower)) ||
                token.Any(char.IsDigit) ||
                token.IndexOfAny(new[] { '.', '+', '#', '/', '-' }) >= 0);
        }

        private static bool IsEnglishTerm(string text)
        {
            if (!PureEnglish.IsMatch(text) || text.Length > 50)
                return false;

            if (text.Contains('.') && text.Contains(' ') && text.Length > 30)
                return false;
            if (text.Contains('?') || text.Contains('!'))
                return false;
            if (double.TryParse(text, out _))
                return false;
            if (!text.Any(char.IsLetter))
                return false;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4 && words.Any(word => SentenceWords.Contains(word.TrimEnd('.'))))
                return false;

            return true;
        }

        private static readonly HashSet<string> SentenceWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "was", "were", "be", "to", "please", "this", "that", "these", "those"
        };

        private enum JsonMatch
        {
            None,
            Valid,
            LargeCandidate
        }
    }
}
