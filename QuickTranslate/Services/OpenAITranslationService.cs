using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// OpenAI-compatible translation service with streaming SSE support.
/// </summary>
public sealed class OpenAITranslationService : ITranslationService, IDisposable
{
    private readonly HttpClient _httpClient;
    private AppSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OpenAITranslationService(AppSettings settings)
        : this(settings, new HttpClientHandler { UseProxy = false })
    {
    }

    internal OpenAITranslationService(AppSettings settings, HttpMessageHandler handler)
    {
        _settings = settings;
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public void UpdateSettings(AppSettings settings)
    {
        Volatile.Write(ref _settings, settings);
    }

    public TranslationRequest CreateRequest(
        string text,
        string targetLang,
        ContentType contentType,
        TranslationRequestKind kind = TranslationRequestKind.Translation)
    {
        var settings = PromptSettings.From(Volatile.Read(ref _settings));
        string prompt;
        var fallbackUsed = false;

        if (kind == TranslationRequestKind.Analysis)
        {
            prompt = BuildAnalysisPrompt(targetLang, settings);
            contentType = ContentType.Analysis;
        }
        else
        {
            var promptResult = BuildSystemPromptCore(targetLang, contentType, text, settings);
            prompt = promptResult.Prompt;
            fallbackUsed = promptResult.FallbackUsed;
        }

        return new TranslationRequest(
            kind,
            text,
            targetLang,
            contentType,
            settings.ApiBaseUrl,
            settings.ApiKey,
            settings.ModelName,
            prompt,
            fallbackUsed);
    }

    public async Task<string> ExecuteStreamingAsync(
        TranslationRequest request,
        Action<string> onChunk,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var operation = request.Kind == TranslationRequestKind.Analysis ? "解析" : "翻译";
        var truncatedInput = request.Text.Length > 80 ? request.Text[..80] + "..." : request.Text;
        Logger.Info("TranslationService", $"[{operation}] {request.ContentType} | {request.TargetLanguage} | \"{truncatedInput}\"");

        var requestBody = BuildRequestBody(request, stream: true);
        using var response = await SendAsync(
            request,
            requestBody,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{operation}请求失败 ({(int)response.StatusCode}): {errorBody}");
        }

        var fullResult = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            try
            {
                using var document = JsonDocument.Parse(data);
                var choices = document.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0)
                    continue;

                var delta = choices[0].GetProperty("delta");
                if (!delta.TryGetProperty("content", out var contentElement))
                    continue;

                var chunk = contentElement.GetString();
                if (string.IsNullOrEmpty(chunk))
                    continue;

                fullResult.Append(chunk);
                onChunk(fullResult.ToString());
            }
            catch (JsonException)
            {
                // Ignore malformed provider chunks and continue reading the stream.
            }
        }

        var result = fullResult.ToString().Trim();
        var truncatedResult = result.Length > 100 ? result[..100] + "..." : result;
        Logger.Info("TranslationService", $"[{operation}结果] \"{truncatedResult}\"");
        return result;
    }

    public Task<string> TranslateStreamingAsync(
        string text,
        string targetLang,
        Action<string> onChunk,
        ContentType contentType = ContentType.Translation,
        Action? onFallbackUsed = null,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(text, targetLang, contentType);
        if (request.FallbackUsed)
            onFallbackUsed?.Invoke();
        return ExecuteStreamingAsync(request, onChunk, cancellationToken);
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLang,
        ContentType contentType = ContentType.Translation,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(text, targetLang, contentType);
        ValidateRequest(request);

        var requestBody = BuildRequestBody(request, stream: false);
        using var response = await SendAsync(
            request,
            requestBody,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"翻译请求失败 ({(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ExtractTranslation(responseBody);
    }

    public Task<string> AnalyzeStreamingAsync(
        string text,
        string targetLang,
        Action<string> onChunk,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(
            text,
            targetLang,
            ContentType.Analysis,
            TranslationRequestKind.Analysis);
        return ExecuteStreamingAsync(request, onChunk, cancellationToken);
    }

    internal string BuildSystemPrompt(
        string targetLang,
        ContentType contentType,
        string sourceText,
        Action? onFallbackUsed = null)
    {
        var request = CreateRequest(sourceText, targetLang, contentType);
        if (request.FallbackUsed)
            onFallbackUsed?.Invoke();
        return request.SystemPrompt;
    }

    private async Task<HttpResponseMessage> SendAsync(
        TranslationRequest request,
        Dictionary<string, object> requestBody,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var url = $"{request.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("Authorization", $"Bearer {request.ApiKey}");
        return await _httpClient.SendAsync(message, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object> BuildRequestBody(TranslationRequest request, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = request.ModelName,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.Text }
            },
            ["temperature"] = 0.3,
            ["stream"] = stream
        };

        if (request.ApiBaseUrl.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        else if (request.ApiBaseUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            body["enable_thinking"] = false;

        return body;
    }

    private static PromptResult BuildSystemPromptCore(
        string targetLang,
        ContentType contentType,
        string sourceText,
        PromptSettings settings)
    {
        var sourceMatchesTarget = contentType == ContentType.Translation &&
                                  settings.AutoDetectLanguage &&
                                  TextMatchesLanguage(sourceText, targetLang);
        var effectiveTarget = sourceMatchesTarget ? settings.FallbackLanguage : targetLang;
        string prompt;

        if (contentType == ContentType.Code)
        {
            prompt = $"You are a code and command explanation assistant. Analyze the input as code, a script, SQL, configuration, or a terminal command. Explain what it does in {targetLang}. " +
                     $"For terminal commands, explain each command, option, pipe, redirect, and important side effect in {targetLang}. " +
                     "Do not translate the source code. Do not repeat the full source. Do not output the source unchanged. If a tiny snippet is necessary, quote only that snippet and explain it. " +
                     "Return a concise explanation directly, without labels, preambles, or markdown headers.";
        }
        else if (contentType == ContentType.Term)
        {
            prompt = $"You are a knowledge assistant. Give a concise explanation of this term in {targetLang} (what it is, 1-2 sentences). " +
                     "Output only the explanation directly. No prefixes, no markdown headers.";
        }
        else if (!string.IsNullOrWhiteSpace(settings.CustomTranslationPrompt))
        {
            prompt = settings.CustomTranslationPrompt.Replace("{targetLang}", effectiveTarget);
        }
        else if (settings.SmartContentType)
        {
            prompt = $"You are a translator. Translate the input to {effectiveTarget}. " +
                     $"If the input is code, explain it briefly in {targetLang} instead. " +
                     "Output only the result directly. No prefixes, no labels.";
        }
        else if (settings.AutoDetectLanguage)
        {
            prompt = $"You are a translator. Translate the input to {effectiveTarget}. " +
                     "You MUST always translate. Never output the original text unchanged. Output only the translation.";
        }
        else
        {
            prompt = $"Translate the input to {targetLang}. If already in {targetLang}, translate to {settings.FallbackLanguage}. " +
                     "You MUST always translate. Never output the original text unchanged. Output only the translation.";
        }

        Logger.Debug(
            "TranslationService",
            $"[Prompt] type={contentType}, target={effectiveTarget}, fallback={settings.FallbackLanguage}, prompt={prompt}");
        return new PromptResult(prompt, sourceMatchesTarget);
    }

    private static string BuildAnalysisPrompt(string targetLang, PromptSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CustomAnalysisPrompt))
            return settings.CustomAnalysisPrompt.Replace("{targetLang}", targetLang);

        return settings.AnalysisPreset switch
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

    private static bool TextMatchesLanguage(string text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasCjk = text.Any(c => c is >= '\u4E00' and <= '\u9FFF');
        var hasKana = text.Any(c => c is >= '\u3040' and <= '\u30FF');
        var hasHangul = text.Any(c => c is >= '\uAC00' and <= '\uD7AF');

        return lang switch
        {
            "简体中文" or "繁体中文" => hasCjk && !hasKana,
            "日本語" => hasKana,
            "한국어" => hasHangul,
            "English" => !hasCjk && !hasKana && !hasHangul,
            _ => false
        };
    }

    private static string ExtractTranslation(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            throw new FormatException($"解析翻译结果失败: {ex.Message}");
        }
    }

    private static void ValidateRequest(TranslationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("请求文本不能为空", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("请先在设置中配置 API Key");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record PromptResult(string Prompt, bool FallbackUsed);

    private sealed record PromptSettings(
        string ApiBaseUrl,
        string ApiKey,
        string ModelName,
        string FallbackLanguage,
        bool AutoDetectLanguage,
        bool SmartContentType,
        string CustomTranslationPrompt,
        string CustomAnalysisPrompt,
        string AnalysisPreset)
    {
        public static PromptSettings From(AppSettings settings)
        {
            return new PromptSettings(
                settings.ApiBaseUrl,
                settings.ApiKey,
                settings.ModelName,
                settings.FallbackLanguage,
                settings.AutoDetectLanguage,
                settings.SmartContentType,
                settings.CustomTranslationPrompt,
                settings.CustomAnalysisPrompt,
                settings.AnalysisPreset);
        }
    }
}
