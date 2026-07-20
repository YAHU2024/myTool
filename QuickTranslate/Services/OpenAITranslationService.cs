using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Services
{
    /// <summary>
    /// OpenAI 兼容接口翻译服务实现（支持流式 SSE）
    /// </summary>
    public class OpenAITranslationService : ITranslationService
    {
        private readonly HttpClient _httpClient;
        private AppSettings _settings;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public OpenAITranslationService(AppSettings settings)
        {
            _settings = settings;
            // 绕过系统代理（避免 Clash 等代理工具干扰 DNS 解析和网络连接）
            var handler = new HttpClientHandler
            {
                UseProxy = false
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        /// <summary>
        /// 更新配置（用于运行时切换 API 设置）
        /// </summary>
        public void UpdateSettings(AppSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// 判断当前 API 是否为智谱（使用 thinking 参数）
        /// </summary>
        private bool IsZhipuApi()
        {
            return _settings.ApiBaseUrl.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断当前 API 是否为硅基流动（使用 enable_thinking 参数）
        /// </summary>
        private bool IsSiliconFlowApi()
        {
            return _settings.ApiBaseUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 构建请求体（根据 API 类型动态添加参数）
        /// </summary>
        private Dictionary<string, object> BuildRequestBody(string text, string targetLang, bool stream, ContentType contentType, Action? onFallbackUsed = null)
        {
            // 构建 system prompt
            var systemPrompt = BuildSystemPrompt(targetLang, contentType, text, onFallbackUsed);

            var body = new Dictionary<string, object>
            {
                ["model"] = _settings.ModelName,
                ["messages"] = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                ["temperature"] = 0.3,
                ["stream"] = stream
            };

            // 智谱 API：使用 thinking.type = "disabled"
            if (IsZhipuApi())
            {
                body["thinking"] = new { type = "disabled" };
            }
            // 硅基流动 API：使用 enable_thinking = false（支持 Qwen3 等模型）
            else if (IsSiliconFlowApi())
            {
                body["enable_thinking"] = false;
            }

            return body;
        }

        /// <summary>
        /// 构建 system prompt（混合方案：本地检测类型 + 分层 Prompt）
        /// </summary>
        private string BuildSystemPrompt(string targetLang, ContentType contentType, string sourceText, Action? onFallbackUsed = null)
        {
            var fallback = _settings.FallbackLanguage;
            Logger.Info("TranslationService", $"[Prompt构建] 目标={targetLang}, 备选={fallback}, " +
                $"ContentType={contentType}, AutoDetect={_settings.AutoDetectLanguage}, " +
                $"SmartContent={_settings.SmartContentType}, CustomPrompt={(!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))}");
        
            // 本地语言检测：判断原文是否已是目标语言
            bool sourceMatchesTarget = _settings.AutoDetectLanguage && TextMatchesLanguage(sourceText, targetLang);
            // 若原文匹配目标语言 → 使用备选语言作为实际翻译方向
            string effectiveTarget = sourceMatchesTarget ? fallback : targetLang;
            Logger.Debug("TranslationService", $"[Prompt构建] 本地语言检测: sourceMatchesTarget={sourceMatchesTarget}, effectiveTarget={effectiveTarget}");

            // 通知调用方触发了兆底（用于显示[解析]标签）
            if (sourceMatchesTarget)
            {
                onFallbackUsed?.Invoke();
            }
        
            string prompt;
        
            // 用户自定义 prompt（支持 {targetLang} 占位符）
            if (!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))
            {
                prompt = _settings.CustomSystemPrompt.Replace("{targetLang}", targetLang);
                Logger.Debug("TranslationService", $"[Prompt构建] 使用自定义Prompt");
            }
            // 本地检测为代码/命令 → 简单解析 Prompt
            else if (contentType == ContentType.Code)
            {
                prompt = $"You are a code assistant. Briefly explain what this code or command does in {targetLang}. " +
                       "If it is a terminal command, explain each parameter. " +
                       "Output only the explanation directly. No prefixes, no markdown headers.";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用Code解析Prompt");
            }
            // 本地检测为纯英文术语 → 简单解释 Prompt
            else if (contentType == ContentType.Term)
            {
                prompt = $"You are a knowledge assistant. Give a concise explanation of this term in {targetLang} (what it is, 1-2 sentences). " +
                       "Output only the explanation directly. No prefixes, no markdown headers.";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用Term解释Prompt");
            }
            // 翻译（默认 / Uncertain 兜底）
            else if (_settings.SmartContentType)
            {
                // 智能路由：翻译为主 + 轻量代码兜底，翻译方向由本地检测决定
                prompt = $"You are a translator. Translate the input to {effectiveTarget}. " +
                       $"Exception: if the input is clearly code or a shell command, briefly explain it in {targetLang} instead. " +
                       "Output only the result directly. No prefixes, no labels.";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用SmartContent翻译Prompt");
            }
            else if (_settings.AutoDetectLanguage)
            {
                // 翻译方向由本地检测决定
                prompt = $"You are a translator. Translate the input to {effectiveTarget}. " +
                       "You MUST always translate. Never output the original text unchanged. Output only the translation.";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用AutoDetect分支");
            }
            else
            {
                // 固定翻译方向
                prompt = $"Translate the input to {targetLang}. If already in {targetLang}, translate to {fallback}. " +
                       "You MUST always translate. Never output the original text unchanged. Output only the translation.";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用固定方向分支");
            }
        
            Logger.Info("TranslationService", $"[Prompt构建] 最终Prompt: {prompt}");
            return prompt;
        }
        
        /// <summary>
        /// 本地启发式判断原文是否匹配目标语言
        /// 用于决定翻译方向，避免模型中不可靠的条件判断
        /// </summary>
        private static bool TextMatchesLanguage(string text, string lang)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
        
            bool hasCJK = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            bool hasKana = text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
            bool hasHangul = text.Any(c => c >= 0xAC00 && c <= 0xD7AF);
        
            return lang switch
            {
                "简体中文" or "繁体中文" => hasCJK && !hasKana,
                "日本語" => hasKana,
                "한국어" => hasHangul,
                "English" => !hasCJK && !hasKana && !hasHangul,
                _ => false
            };
        }

        /// <summary>
        /// 流式翻译 - 通过 SSE 逐步返回翻译结果
        /// </summary>
        public async Task<string> TranslateStreamingAsync(string text, string targetLang, Action<string> onChunk, ContentType contentType = ContentType.Translation, Action? onFallbackUsed = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("请先在设置中配置 API Key");

            var truncatedInput = text.Length > 80 ? text[..80] + "..." : text;
            Logger.Info("TranslationService", $"[翻译请求] 模型={_settings.ModelName}, 目标={targetLang}, " +
                $"备选={_settings.FallbackLanguage}, 类型={contentType}, 输入=\"{truncatedInput}\"");

            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = BuildRequestBody(text, targetLang, stream: true, contentType, onFallbackUsed);

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = httpContent;
            request.Headers.Add("Authorization", $"Bearer {_settings.ApiKey}");

            // HttpCompletionOption.ResponseHeadersRead 让流式数据尽快开始读取
            var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"翻译请求失败 ({(int)response.StatusCode}): {errorBody}");
            }

            var fullResult = new StringBuilder();

            // ★ 关键修复：将流式读取循环移到后台线程
            // 原问题：await 在 UI 线程上执行，当 TCP 缓冲区有多个 SSE 事件时
            // ReadLineAsync() 同步完成 → 不 yield → UI 线程被占满 → 无渲染 pass
            // Task.Run 让循环在后台线程执行，UI 线程完全释放给 Dispatcher 渲染
            await Task.Run(async () =>
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // SSE 格式: "data: {...}" 或 "data: [DONE]"
                    if (!line.StartsWith("data: "))
                        continue;

                    var data = line.Substring(6); // 去掉 "data: " 前缀

                    if (data == "[DONE]")
                        break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var choices = doc.RootElement.GetProperty("choices");
                        if (choices.GetArrayLength() == 0)
                            continue;

                        var delta = choices[0].GetProperty("delta");

                        if (delta.TryGetProperty("content", out var contentElement))
                        {
                            var chunk = contentElement.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                fullResult.Append(chunk);
                                onChunk?.Invoke(fullResult.ToString());
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // 忽略无法解析的 chunk
                    }
                }
            });

            var result = fullResult.ToString().Trim();
            var truncatedResult = result.Length > 100 ? result[..100] + "..." : result;
            Logger.Info("TranslationService", $"[翻译结果] 输出=\"{truncatedResult}\"");
            return result;
        }

        /// <summary>
        /// 非流式翻译（兼容旧逻辑）
        /// </summary>
        public async Task<string> TranslateAsync(string text, string targetLang, ContentType contentType = ContentType.Translation)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("请先在设置中配置 API Key");

            var truncatedInput = text.Length > 80 ? text[..80] + "..." : text;
            Logger.Info("TranslationService", $"[翻译请求-非流式] 模型={_settings.ModelName}, 目标={targetLang}, " +
                $"备选={_settings.FallbackLanguage}, 类型={contentType}, 输入=\"{truncatedInput}\"");

            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = BuildRequestBody(text, targetLang, stream: false, contentType);

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = httpContent;
            request.Headers.Add("Authorization", $"Bearer {_settings.ApiKey}");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"翻译请求失败 ({(int)response.StatusCode}): {errorBody}");
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            return ExtractTranslation(responseBody);
        }

        /// <summary>
        /// 从 OpenAI 非流式响应中提取翻译文本
        /// </summary>
        private static string ExtractTranslation(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return content?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new FormatException($"解析翻译结果失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流式解析文本（用目标语言深度解析）
        /// </summary>
        public async Task<string> AnalyzeStreamingAsync(string text, string targetLang, Action<string> onChunk)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("请先在设置中配置 API Key");

            var truncatedInput = text.Length > 80 ? text[..80] + "..." : text;
            Logger.Info("TranslationService", $"[解析请求] 模型={_settings.ModelName}, 目标={targetLang}, " +
                $"预设={_settings.AnalysisPreset}, 输入=\"{truncatedInput}\"");

            // 构建解析 Prompt
            var systemPrompt = BuildAnalysisPrompt(targetLang);

            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/chat/completions";
            var body = new Dictionary<string, object>
            {
                ["model"] = _settings.ModelName,
                ["messages"] = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                ["temperature"] = 0.3,
                ["stream"] = true
            };

            if (IsZhipuApi())
                body["thinking"] = new { type = "disabled" };
            else if (IsSiliconFlowApi())
                body["enable_thinking"] = false;

            var jsonContent = JsonSerializer.Serialize(body, JsonOptions);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = httpContent;
            request.Headers.Add("Authorization", $"Bearer {_settings.ApiKey}");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"解析请求失败 ({(int)response.StatusCode}): {errorBody}");
            }

            var fullResult = new StringBuilder();

            await Task.Run(async () =>
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var choices = doc.RootElement.GetProperty("choices");
                        if (choices.GetArrayLength() == 0) continue;

                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentElement))
                        {
                            var chunk = contentElement.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                fullResult.Append(chunk);
                                onChunk?.Invoke(fullResult.ToString());
                            }
                        }
                    }
                    catch (JsonException) { }
                }
            });

            var result = fullResult.ToString().Trim();
            var truncatedResult = result.Length > 100 ? result[..100] + "..." : result;
            Logger.Info("TranslationService", $"[解析结果] 输出=\"{truncatedResult}\"");
            return result;
        }

        /// <summary>
        /// 构建解析 Prompt（根据预设或自定义）
        /// </summary>
        private string BuildAnalysisPrompt(string targetLang)
        {
            // 用户自定义 Prompt 优先
            if (!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))
            {
                return _settings.CustomSystemPrompt.Replace("{targetLang}", targetLang);
            }

            // 根据预设选择 Prompt
            return _settings.AnalysisPreset switch
            {
                "learner" => $"You are a language tutor. Analyze the following text in {targetLang}. " +
                    "Provide: 1) Word-by-word breakdown, 2) Grammar explanation, " +
                    "3) Common usage patterns, 4) Pronunciation tips if applicable. " +
                    "Output only the analysis directly. No prefixes, no markdown headers.",

                "literary" => $"You are a literary scholar. Analyze the following text in {targetLang}. " +
                    "Provide: 1) Rhetorical devices used, 2) Literary imagery and symbolism, " +
                    "3) Cultural and historical context, 4) Stylistic features. " +
                    "Output only the analysis directly. No prefixes, no markdown headers.",

                "business" => $"You are a business communication expert. Analyze the following text in {targetLang}. " +
                    "Provide: 1) Core business meaning, 2) Industry terminology, " +
                    "3) Action items or implications, 4) Professional context. " +
                    "Output only the analysis directly. No prefixes, no markdown headers.",

                _ => $"You are a knowledgeable analyst. Analyze the following text in {targetLang}. " +
                    "Provide: 1) Core meaning and key points, 2) Grammar and structure analysis, " +
                    "3) Context and background information. " +
                    "Output only the analysis directly. No prefixes, no markdown headers."
            };
        }
    }
}
