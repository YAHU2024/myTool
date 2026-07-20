using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
            // 用户自定义 prompt（支持 {targetLang} 占位符）
            if (!string.IsNullOrWhiteSpace(_settings.CustomSystemPrompt))
            {
                return _settings.CustomSystemPrompt.Replace("{targetLang}", targetLang);
            }

            // 默认 prompt
            if (_settings.AutoDetectLanguage)
            {
                // 语言自动检测：尊重用户在设置中配置的目标语言
                return "You are a translator. First detect the language of the input text. " +
                       "If the text is Chinese, translate it to English. " +
                       $"If the text is NOT Chinese, translate it to {targetLang}. " +
                       "IMPORTANT: You MUST always translate, even if the text is already in the target language. " +
                       "Never output the original text unchanged. Output only the translation.";
            }
            else
            {
                // 固定翻译方向
                return $"Translate to {targetLang}. If already in {targetLang}, translate to English. " +
                       "IMPORTANT: You MUST always translate. Never output the original text unchanged. Output only the translation.";
            }
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

            return fullResult.ToString().Trim();
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
