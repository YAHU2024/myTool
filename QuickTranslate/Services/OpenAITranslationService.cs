using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
        private Dictionary<string, object> BuildRequestBody(string text, string targetLang, bool stream)
        {
            // 构建 system prompt
            var systemPrompt = BuildSystemPrompt(targetLang);

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
        /// 构建 system prompt（支持自定义和语言自动检测）
        /// </summary>
        private string BuildSystemPrompt(string targetLang)
        {
            var fallback = _settings.FallbackLanguage;
            Logger.Info("TranslationService", $"[Prompt构建] 目标语言={targetLang}, 备选语言={fallback}, " +
                $"AutoDetect={_settings.AutoDetectLanguage}, SmartContent={_settings.SmartContentType}, " +
                $"CustomPrompt={(!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))}");

            string prompt;

            // 用户自定义 prompt（支持 {targetLang} 占位符）
            if (!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))
            {
                prompt = _settings.CustomSystemPrompt.Replace("{targetLang}", targetLang);
                Logger.Debug("TranslationService", $"[Prompt构建] 使用自定义Prompt");
            }
            // 智能内容识别：代码/命令→解析，纯英文术语→解释，其余→翻译
            else if (_settings.SmartContentType)
            {
                var translateRule = _settings.AutoDetectLanguage
                    ? $"其他所有情况：翻译为{targetLang}。如果原文已经是{targetLang}，则翻译为{fallback}。"
                    : $"其他所有情况：翻译为{targetLang}。";

                prompt = $"你是一个翻译助手。对输入文本执行以下操作：\n" +
                       $"- 如果是代码或终端命令：用{targetLang}简要解释其功能。\n" +
                       $"- 如果是纯英文的专有名词或技术术语（非句子）：用{targetLang}简要解释（1-2句）。\n" +
                       $"- {translateRule}\n" +
                       $"严格要求：直接输出结果，禁止添加任何前缀、标签或解释性语句。";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用SmartContent分支");
            }
            // 自动检测语言方向
            else if (_settings.AutoDetectLanguage)
            {
                prompt = $"你是翻译器。将输入文本翻译为{targetLang}。如果原文已经是{targetLang}，则翻译为{fallback}。\n" +
                       "严格要求：必须翻译，禁止原样输出，直接输出译文。";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用AutoDetect分支");
            }
            else
            {
                // 固定翻译方向
                prompt = $"将输入文本翻译为{targetLang}。如果原文已经是{targetLang}，则翻译为{fallback}。\n" +
                       "严格要求：必须翻译，禁止原样输出，直接输出译文。";
                Logger.Debug("TranslationService", $"[Prompt构建] 使用固定方向分支");
            }

            Logger.Info("TranslationService", $"[Prompt构建] 最终Prompt: {prompt}");
            return prompt;
        }

        /// <summary>
        /// 流式翻译 - 通过 SSE 逐步返回翻译结果
        /// </summary>
        public async Task<string> TranslateStreamingAsync(string text, string targetLang, Action<string> onChunk)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("请先在设置中配置 API Key");

            var truncatedInput = text.Length > 80 ? text[..80] + "..." : text;
            Logger.Info("TranslationService", $"[翻译请求] 模型={_settings.ModelName}, 目标={targetLang}, " +
                $"备选={_settings.FallbackLanguage}, 输入=\"{truncatedInput}\"");

            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = BuildRequestBody(text, targetLang, stream: true);

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
        public async Task<string> TranslateAsync(string text, string targetLang)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("请先在设置中配置 API Key");

            var truncatedInput = text.Length > 80 ? text[..80] + "..." : text;
            Logger.Info("TranslationService", $"[翻译请求-非流式] 模型={_settings.ModelName}, 目标={targetLang}, " +
                $"备选={_settings.FallbackLanguage}, 输入=\"{truncatedInput}\"");

            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = BuildRequestBody(text, targetLang, stream: false);

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
    }
}
