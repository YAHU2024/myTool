using System.IO;
using System.Diagnostics;
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

        var request = new TranslationRequest(
            kind,
            text,
            targetLang,
            contentType,
            settings.ApiBaseUrl,
            settings.ApiKey,
            settings.ModelName,
            prompt,
            fallbackUsed);
        Logger.Debug(
            "TranslationService",
            "prompt.selected",
            BuildPromptLogContext(
                request,
                !string.IsNullOrWhiteSpace(settings.CustomTranslationPrompt),
                settings.SelectedAnalysisPromptId.StartsWith("custom:", StringComparison.Ordinal),
                settings.SelectedAnalysisPromptId));
        return request;
    }

    public async Task<string> ExecuteStreamingAsync(
        TranslationRequest request,
        Action<string> onChunk,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var operation = request.Kind == TranslationRequestKind.Analysis ? "analysis" : "translation";
        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("TranslationService", "translation.started", new
        {
            operation,
            content_type = request.ContentType.ToString(),
            target_language = request.TargetLanguage,
            text_len = request.Text.Length
        });

        var requestBody = BuildRequestBody(request, stream: true);
        using var response = await SendAsync(
            request,
            requestBody,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} request failed ({(int)response.StatusCode})");
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
        Logger.Info("TranslationService", "translation.completed", new
        {
            operation,
            content_type = request.ContentType.ToString(),
            target_language = request.TargetLanguage,
            text_len = request.Text.Length,
            result_len = result.Length,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
        });
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
            throw new HttpRequestException(
                $"translation request failed ({(int)response.StatusCode})");
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
            prompt = $"Explain this code, script, SQL, configuration, or terminal command in {targetLang}. " +
                     "For commands, cover each command, option, pipe, redirect, and important side effect. " +
                     "Do not translate or reproduce the full source; quote only tiny snippets when necessary. " +
                     "Output a concise explanation with no preamble, labels, or markdown headers.";
        }
        else if (contentType == ContentType.Term)
        {
            prompt = $"Explain this term in {targetLang} in 1-2 concise sentences: what it is and its main use. " +
                     "Output only the explanation; no preamble or markdown headers.";
        }
        else if (!string.IsNullOrWhiteSpace(settings.CustomTranslationPrompt))
        {
            prompt = settings.CustomTranslationPrompt.Replace("{targetLang}", effectiveTarget);
        }
        else if (settings.AutoDetectLanguage)
        {
            prompt = $"Translate the input into {effectiveTarget}. " +
                     "Always translate; never return the original unchanged. Output only the translation.";
        }
        else
        {
            prompt = $"Translate the input into {targetLang}. If it is already in {targetLang}, translate it into {settings.FallbackLanguage}. " +
                     "Always translate; never return the original unchanged. Output only the translation.";
        }
        return new PromptResult(prompt, sourceMatchesTarget);
    }

    private static string BuildAnalysisPrompt(string targetLang, PromptSettings settings)
    {
        return AnalysisPromptCatalog.Resolve(
            settings.SelectedAnalysisPromptId,
            settings.AnalysisPromptProfiles,
            targetLang);
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

    internal static IReadOnlyDictionary<string, object?> BuildPromptLogContext(
        TranslationRequest request,
        bool customTranslationPrompt,
        bool customAnalysisPrompt,
        string analysisPreset)
    {
        return new Dictionary<string, object?>
        {
            ["content_type"] = request.ContentType.ToString(),
            ["request_kind"] = request.Kind.ToString(),
            ["target_language"] = request.TargetLanguage,
            ["fallback_used"] = request.FallbackUsed,
            ["custom_translation_prompt"] = customTranslationPrompt,
            ["custom_analysis_prompt"] = customAnalysisPrompt,
            ["analysis_preset"] = analysisPreset,
            ["prompt_len"] = request.SystemPrompt.Length
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
        string SelectedAnalysisPromptId,
        IReadOnlyList<AnalysisPromptProfile> AnalysisPromptProfiles)
    {
        public static PromptSettings From(AppSettings settings)
        {
            var selectedAnalysisPromptId = string.IsNullOrWhiteSpace(settings.SelectedAnalysisPromptId)
                ? AnalysisPromptCatalog.GeneralId
                : settings.SelectedAnalysisPromptId;
            return new PromptSettings(
                settings.ApiBaseUrl,
                settings.ApiKey,
                settings.ModelName,
                settings.FallbackLanguage,
                settings.AutoDetectLanguage,
                settings.SmartContentType,
                settings.CustomTranslationPrompt,
                selectedAnalysisPromptId,
                (settings.AnalysisPromptProfiles ?? new List<AnalysisPromptProfile>())
                    .Select(profile => profile.Clone())
                    .ToArray());
        }
    }
}
