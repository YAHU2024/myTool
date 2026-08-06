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
    internal const int MaxFollowUpQuestionRunes = AnalysisConversationFormatter.MaxQuestionRunes;
    internal const int MaxFollowUpContextCharacters = 60000;

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
        Action<string> onDelta,
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

        var execution = await ExecuteChatStreamingAsync(
            request.ApiBaseUrl,
            request.ApiKey,
            BuildRequestBody(request, stream: true),
            operation,
            onDelta,
            cancellationToken).ConfigureAwait(false);
        var result = execution.Result;
        Logger.Info("TranslationService", "translation.completed", new
        {
            operation,
            content_type = request.ContentType.ToString(),
            target_language = request.TargetLanguage,
            text_len = request.Text.Length,
            result_len = result.Length,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            stream_chunk_count = execution.ChunkCount,
            first_chunk_ms = execution.FirstChunkMs,
            max_chunk_gap_ms = execution.MaxChunkGapMs
        });
        return result;
    }

    public AnalysisFollowUpRequest CreateAnalysisFollowUpRequest(
        string sourceText,
        string rootAnalysis,
        AnalysisSemanticSnapshot semanticSnapshot,
        IReadOnlyList<AnalysisFollowUpExchange> completedTurns,
        string question,
        int turnNumber,
        long requestId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAnalysis);
        ArgumentNullException.ThrowIfNull(semanticSnapshot);
        ArgumentNullException.ThrowIfNull(completedTurns);

        if (string.IsNullOrWhiteSpace(semanticSnapshot.SystemPrompt))
            throw new ArgumentException("解析 Prompt 不能为空", nameof(semanticSnapshot));
        if (turnNumber is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(turnNumber));
        if (completedTurns.Count > 9)
            throw new ArgumentOutOfRangeException(nameof(completedTurns));

        var normalizedQuestion = AnalysisConversationFormatter.NormalizeQuestion(question);

        var messages = new List<ChatCompletionMessage>(4 + completedTurns.Count * 2)
        {
            new("system", semanticSnapshot.SystemPrompt),
            new("user", sourceText),
            new("assistant", rootAnalysis)
        };
        foreach (var turn in completedTurns)
        {
            if (string.IsNullOrWhiteSpace(turn.Question) || string.IsNullOrWhiteSpace(turn.Answer))
                throw new ArgumentException("已完成追问必须包含问题和回答", nameof(completedTurns));
            messages.Add(new ChatCompletionMessage("user", turn.Question));
            messages.Add(new ChatCompletionMessage("assistant", turn.Answer));
        }
        messages.Add(new ChatCompletionMessage("user", normalizedQuestion));

        var contextCharacters = messages.Sum(message => message.Content.Length);
        if (contextCharacters > MaxFollowUpContextCharacters)
        {
            Logger.Info("TranslationService", "analysis.follow_up.limit_reached", new
            {
                turn_count = turnNumber,
                context_chars = contextCharacters,
                limit_kind = "context_chars",
                request_id = requestId
            });
            throw new InvalidOperationException("当前解析内容过长，无法继续追问");
        }

        var settings = PromptSettings.From(Volatile.Read(ref _settings));
        return new AnalysisFollowUpRequest(
            turnNumber,
            messages.ToArray(),
            settings.ApiBaseUrl,
            settings.ApiKey,
            settings.ModelName,
            normalizedQuestion.Length,
            contextCharacters,
            requestId);
    }

    public async Task<string> ExecuteAnalysisFollowUpStreamingAsync(
        AnalysisFollowUpRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ValidateFollowUpRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("TranslationService", "analysis.follow_up.started", new
        {
            turn = request.TurnNumber,
            question_len = request.QuestionLength,
            context_chars = request.ContextCharacters,
            request_id = request.RequestId
        });

        try
        {
            var execution = await ExecuteChatStreamingAsync(
                request.ApiBaseUrl,
                request.ApiKey,
                BuildRequestBody(
                    request.ModelName,
                    request.Messages,
                    request.ApiBaseUrl,
                    stream: true),
                "analysis follow-up",
                onDelta,
                cancellationToken).ConfigureAwait(false);
            var result = execution.Result;
            if (string.IsNullOrWhiteSpace(result))
                throw new FormatException("追问返回为空");

            Logger.Info("TranslationService", "analysis.follow_up.completed", new
            {
                turn = request.TurnNumber,
                answer_len = result.Length,
                duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                stream_chunk_count = execution.ChunkCount,
                first_chunk_ms = execution.FirstChunkMs,
                max_chunk_gap_ms = execution.MaxChunkGapMs,
                request_id = request.RequestId
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("TranslationService", "analysis.follow_up.cancelled", new
            {
                turn = request.TurnNumber,
                request_id = request.RequestId
            });
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn("TranslationService", "analysis.follow_up.failed", new
            {
                turn = request.TurnNumber,
                error_type = ex.GetType().Name,
                request_id = request.RequestId,
                status_code = ex is HttpRequestException { StatusCode: { } statusCode }
                    ? (int?)statusCode
                    : null
            });
            throw;
        }
    }

    public async Task<string> TranslateStreamingAsync(
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
        var accumulated = new StringBuilder();
        return await ExecuteStreamingAsync(
            request,
            delta =>
            {
                accumulated.Append(delta);
                onChunk(accumulated.ToString());
            },
            cancellationToken).ConfigureAwait(false);
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
            request.ApiBaseUrl,
            request.ApiKey,
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

    public async Task<string> AnalyzeStreamingAsync(
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
        var accumulated = new StringBuilder();
        return await ExecuteStreamingAsync(
            request,
            delta =>
            {
                accumulated.Append(delta);
                onChunk(accumulated.ToString());
            },
            cancellationToken).ConfigureAwait(false);
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
        string apiBaseUrl,
        string apiKey,
        Dictionary<string, object> requestBody,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/chat/completions";
        var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("Authorization", $"Bearer {apiKey}");
        return await _httpClient.SendAsync(message, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object> BuildRequestBody(TranslationRequest request, bool stream)
    {
        return BuildRequestBody(
            request.ModelName,
            [
                new ChatCompletionMessage("system", request.SystemPrompt),
                new ChatCompletionMessage("user", request.Text)
            ],
            request.ApiBaseUrl,
            stream);
    }

    private static Dictionary<string, object> BuildRequestBody(
        string modelName,
        IReadOnlyList<ChatCompletionMessage> messages,
        string apiBaseUrl,
        bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = modelName,
            ["messages"] = messages,
            ["temperature"] = 0.3,
            ["stream"] = stream
        };

        if (apiBaseUrl.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        else if (apiBaseUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            body["enable_thinking"] = false;

        return body;
    }

    private async Task<ChatStreamingResult> ExecuteChatStreamingAsync(
        string apiBaseUrl,
        string apiKey,
        Dictionary<string, object> requestBody,
        string operation,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        var streamStartedAt = Stopwatch.GetTimestamp();
        using var response = await SendAsync(
            apiBaseUrl,
            apiKey,
            requestBody,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} request failed ({(int)response.StatusCode})",
                inner: null,
                response.StatusCode);
        }

        var fullResult = new StringBuilder();
        var chunkCount = 0;
        double? firstChunkMs = null;
        var maxChunkGapMs = 0.0;
        long? previousChunkAt = null;
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
                var chunkAt = Stopwatch.GetTimestamp();
                firstChunkMs ??= Stopwatch.GetElapsedTime(streamStartedAt, chunkAt).TotalMilliseconds;
                if (previousChunkAt is { } previous)
                {
                    maxChunkGapMs = Math.Max(
                        maxChunkGapMs,
                        Stopwatch.GetElapsedTime(previous, chunkAt).TotalMilliseconds);
                }
                previousChunkAt = chunkAt;
                chunkCount++;
                fullResult.Append(chunk);
                onDelta(chunk);
            }
            catch (JsonException)
            {
                // Ignore malformed provider chunks and continue reading the stream.
            }
        }

        return new ChatStreamingResult(
            fullResult.ToString().Trim(),
            chunkCount,
            firstChunkMs,
            maxChunkGapMs);
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

        // Enforce HTTPS for remote endpoints — credentials must not travel
        // as plaintext over the network. HTTP is only permitted for
        // loopback addresses used during local development.
        ApiEndpointValidator.ValidateAndNormalize(request.ApiBaseUrl);
    }

    private static void ValidateFollowUpRequest(AnalysisFollowUpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count < 4)
            throw new ArgumentException("追问上下文不完整", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("请先在设置中配置 API Key");
        ApiEndpointValidator.ValidateAndNormalize(request.ApiBaseUrl);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record PromptResult(string Prompt, bool FallbackUsed);

    private sealed record ChatStreamingResult(
        string Result,
        int ChunkCount,
        double? FirstChunkMs,
        double MaxChunkGapMs);

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
