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
public sealed class OpenAITranslationService : ITranslationService, IScreenshotBatchTranslationService, IScreenshotBatchStreamingTranslationService, IDisposable
{
    internal const int MaxInitialRequestRunes = 20000;
    internal const int MaxAnalysisRequestRunes = 30000;
    internal const int MaxFollowUpQuestionRunes = AnalysisConversationFormatter.MaxQuestionRunes;
    internal const int MaxFollowUpContextCharacters = 60000;
    internal const double StalledChunkGapThresholdMs = 250;
    internal static readonly TimeSpan ScreenshotFirstChunkTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ScreenshotIdleChunkTimeout = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan ScreenshotOverallTimeout = TimeSpan.FromSeconds(55);

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
        return CreateRequest(text, contentType, kind, CaptureRequestContext(targetLang));
    }

    internal TranslationRequestContext CaptureRequestContext(string targetLang)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLang);
        var settings = Volatile.Read(ref _settings);
        var selectedAnalysisPromptId = string.IsNullOrWhiteSpace(settings.SelectedAnalysisPromptId)
            ? AnalysisPromptCatalog.GeneralId
            : settings.SelectedAnalysisPromptId;
        return new TranslationRequestContext(
            settings.ApiBaseUrl,
            settings.ApiKey,
            settings.ModelName,
            ProviderRequestPolicy.ResolveThinkingRequestValue(
                settings.ApiBaseUrl,
                settings.ModelName,
                settings.ThinkingMode),
            targetLang,
            settings.FallbackLanguage,
            settings.AutoDetectLanguage,
            settings.CustomTranslationPrompt,
            selectedAnalysisPromptId,
            (settings.AnalysisPromptProfiles ?? new List<AnalysisPromptProfile>())
                .Select(profile => profile.Clone())
                .ToArray());
    }

    internal TranslationRequest CreateRequest(
        string text,
        ContentType contentType,
        TranslationRequestKind kind,
        TranslationRequestContext context,
        TranslationDirectionPreference directionPreference = TranslationDirectionPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        var maxRunes = kind == TranslationRequestKind.Analysis || contentType == ContentType.Analysis
            ? MaxAnalysisRequestRunes
            : MaxInitialRequestRunes;
        EnsureInputLength(text, maxRunes, kind == TranslationRequestKind.Analysis ? "解析" : "请求");
        string prompt;
        TranslationDirectionDecision direction;

        if (kind == TranslationRequestKind.Analysis)
        {
            contentType = ContentType.Analysis;
            direction = TranslationDirectionResolver.Resolve(
                text,
                context.RequestedTargetLanguage,
                context.FallbackLanguage,
                context.AutoDetectLanguage,
                contentType,
                directionPreference);
            prompt = BuildAnalysisPrompt(context.RequestedTargetLanguage, context);
        }
        else
        {
            direction = TranslationDirectionResolver.Resolve(
                text,
                context.RequestedTargetLanguage,
                context.FallbackLanguage,
                context.AutoDetectLanguage,
                contentType,
                directionPreference);
            prompt = TranslationPromptBuilder.Build(
                contentType,
                direction.EffectiveTargetLanguage,
                context.CustomTranslationPrompt,
                directionPreference == TranslationDirectionPreference.FixedRequestedTarget);
        }

        var request = new TranslationRequest(
            kind,
            text,
            direction,
            contentType,
            context.ApiBaseUrl,
            context.ApiKey,
            context.ModelName,
            prompt,
            context.EnableThinking);
        Logger.Debug(
            "TranslationService",
            "prompt.selected",
            BuildPromptLogContext(
                request,
                !string.IsNullOrWhiteSpace(context.CustomTranslationPrompt),
                context.SelectedAnalysisPromptId.StartsWith("custom:", StringComparison.Ordinal),
                context.SelectedAnalysisPromptId));
        return request;
    }

    public async Task<string> ExecuteStreamingAsync(
        TranslationRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        return await ExecuteStreamingEventsAsync(
            request,
            streamEvent =>
            {
                if (streamEvent.Kind == TranslationStreamEventKind.ContentDelta &&
                    !string.IsNullOrEmpty(streamEvent.Text))
                {
                    onDelta(streamEvent.Text);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteStreamingEventsAsync(
        TranslationRequest request,
        Action<TranslationStreamEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var operation = request.Kind == TranslationRequestKind.Analysis ? "analysis" : "translation";
        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("TranslationService", "translation.started", new
        {
            operation,
            content_type = request.ContentType.ToString(),
            requested_target_language = request.RequestedTargetLanguage,
            effective_target_language = request.EffectiveTargetLanguage,
            direction_relation = request.Direction.Relation.ToString(),
            direction_confidence = request.Direction.Confidence.ToString(),
            direction_reason = request.Direction.Reason.ToString(),
            source_language_family = request.Direction.SourceLanguageFamily.ToString(),
            text_len = request.Text.Length
        });

        var execution = await ExecuteChatStreamingAsync(
            request.ApiBaseUrl,
            request.ApiKey,
            BuildRequestBody(request, stream: true),
            operation,
            onEvent,
            cancellationToken).ConfigureAwait(false);
        var result = execution.Result;
        Logger.Info("TranslationService", "translation.completed", new
        {
            operation,
            content_type = request.ContentType.ToString(),
            requested_target_language = request.RequestedTargetLanguage,
            effective_target_language = request.EffectiveTargetLanguage,
            direction_relation = request.Direction.Relation.ToString(),
            direction_confidence = request.Direction.Confidence.ToString(),
            direction_reason = request.Direction.Reason.ToString(),
            source_language_family = request.Direction.SourceLanguageFamily.ToString(),
            text_len = request.Text.Length,
            result_len = result.Length,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            stream_chunk_count = execution.ChunkCount,
            first_chunk_ms = execution.FirstChunkMs,
            average_chunk_gap_ms = execution.AverageChunkGapMs,
            max_chunk_gap_ms = execution.MaxChunkGapMs,
            stalled_chunk_count = execution.StalledChunkCount
        });
        onEvent(new TranslationStreamEvent(TranslationStreamEventKind.Completed));
        return result;
    }

    public AnalysisFollowUpRequest CreateAnalysisFollowUpRequest(
        string sourceText,
        string rootAnalysis,
        AnalysisSemanticSnapshot semanticSnapshot,
        IReadOnlyList<AnalysisFollowUpExchange> completedTurns,
        string question,
        int turnNumber,
        long requestId = 0,
        TranslationRequestContext? requestContext = null)
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

        var contextCharacters = messages.Sum(message => message.Content.EnumerateRunes().Count());
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

        var context = requestContext ?? CaptureRequestContext(semanticSnapshot.TargetLanguage);
        return new AnalysisFollowUpRequest(
            turnNumber,
            messages.ToArray(),
            context.ApiBaseUrl,
            context.ApiKey,
            context.ModelName,
            normalizedQuestion.Length,
            contextCharacters,
            requestId,
            context.EnableThinking);
    }

    public async Task<string> ExecuteAnalysisFollowUpStreamingAsync(
        AnalysisFollowUpRequest request,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        return await ExecuteAnalysisFollowUpStreamingEventsAsync(
            request,
            streamEvent =>
            {
                if (streamEvent.Kind == TranslationStreamEventKind.ContentDelta &&
                    !string.IsNullOrEmpty(streamEvent.Text))
                {
                    onDelta(streamEvent.Text);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteAnalysisFollowUpStreamingEventsAsync(
        AnalysisFollowUpRequest request,
        Action<TranslationStreamEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        ValidateFollowUpRequest(request);
        ArgumentNullException.ThrowIfNull(onEvent);
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
                    request.EnableThinking,
                    stream: true),
                "analysis follow-up",
                onEvent,
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
                average_chunk_gap_ms = execution.AverageChunkGapMs,
                max_chunk_gap_ms = execution.MaxChunkGapMs,
                stalled_chunk_count = execution.StalledChunkCount,
                request_id = request.RequestId
            });
            onEvent(new TranslationStreamEvent(TranslationStreamEventKind.Completed));
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
        return await TranslateAsyncCore(
            text,
            targetLang,
            contentType,
            TranslationDirectionPreference.Auto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 翻译为指定目标语言，不执行“同语言切换到备选语言”的普通划词策略。
    /// 截图翻译使用此入口，确保一张截图中的所有单元保持同一输出语言。
    /// </summary>
    public async Task<string> TranslateToRequestedTargetAsync(
        string text,
        string targetLang,
        ContentType contentType = ContentType.Translation,
        CancellationToken cancellationToken = default)
    {
        return await TranslateAsyncCore(
            text,
            targetLang,
            contentType,
            TranslationDirectionPreference.FixedRequestedTarget,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TranslatedTextUnit>> TranslateScreenshotBatchAsync(
        IReadOnlyList<ScreenshotTranslationUnit> units,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        cancellationToken.ThrowIfCancellationRequested();
        if (units.Count == 0)
            return Array.Empty<TranslatedTextUnit>();

        ValidateScreenshotBatchUnits(units);
        var context = CaptureRequestContext(targetLanguage);
        var batchInput = BuildScreenshotBatchInput(units);
        EnsureInputLength(batchInput, MaxInitialRequestRunes, "截图批量请求");
        var direction = TranslationDirectionResolver.Resolve(
            batchInput,
            targetLanguage,
            context.FallbackLanguage,
            context.AutoDetectLanguage,
            ContentType.Translation,
            TranslationDirectionPreference.FixedRequestedTarget);
        var request = new TranslationRequest(
            TranslationRequestKind.Translation,
            batchInput,
            direction,
            ContentType.Translation,
            context.ApiBaseUrl,
            context.ApiKey,
            context.ModelName,
            TranslationPromptBuilder.BuildScreenshotBatchPrompt(
                targetLanguage,
                context.CustomTranslationPrompt),
            context.EnableThinking);
        ValidateRequest(request);

        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("TranslationService", "screenshot_batch.started", new
        {
            unit_count = units.Count,
            text_len = units.Sum(static unit => unit.SourceText.Length),
            requested_target_language = targetLanguage
        });

        string responseBody;
        using (var response = await SendAsync(
                   request.ApiBaseUrl,
                   request.ApiKey,
                   BuildRequestBody(
                       request.ModelName,
                       [
                           new ChatCompletionMessage("system", request.SystemPrompt),
                           new ChatCompletionMessage("user", PromptInputContract.Wrap(request.Text))
                       ],
                       request.ApiBaseUrl,
                       request.EnableThinking,
                       stream: false),
                   HttpCompletionOption.ResponseContentRead,
                   cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ProviderHttpError.CreateExceptionAsync(
                    "screenshot batch translation",
                    response,
                    cancellationToken).ConfigureAwait(false);
            }

            responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        string content;
        try
        {
            content = ExtractTranslation(responseBody);
        }
        catch (FormatException ex)
        {
            throw new ScreenshotTranslationBatchFormatException("invalid_provider_response", ex);
        }

        var mapped = ScreenshotTranslationMapper.ParseAndMap(content, units);
        if (!mapped.Accepted)
            throw new ScreenshotTranslationBatchFormatException(mapped.Reason);

        Logger.Info("TranslationService", "screenshot_batch.completed", new
        {
            unit_count = units.Count,
            mapped_count = mapped.MappedCount,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            requested_target_language = targetLanguage
        });
        return mapped.MappedUnits;
    }

    public async Task<IReadOnlyList<TranslatedTextUnit>> TranslateScreenshotBatchStreamingAsync(
        IReadOnlyList<ScreenshotTranslationUnit> units,
        string targetLanguage,
        Action<TranslatedTextUnit> onUnitCompleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(onUnitCompleted);
        cancellationToken.ThrowIfCancellationRequested();
        if (units.Count == 0)
            return Array.Empty<TranslatedTextUnit>();

        ValidateScreenshotBatchUnits(units);
        var context = CaptureRequestContext(targetLanguage);
        var batchInput = BuildScreenshotBatchInput(units);
        EnsureInputLength(batchInput, MaxInitialRequestRunes, "截图批量流式请求");
        var direction = TranslationDirectionResolver.Resolve(
            batchInput,
            targetLanguage,
            context.FallbackLanguage,
            context.AutoDetectLanguage,
            ContentType.Translation,
            TranslationDirectionPreference.FixedRequestedTarget);
        var request = new TranslationRequest(
            TranslationRequestKind.Translation,
            batchInput,
            direction,
            ContentType.Translation,
            context.ApiBaseUrl,
            context.ApiKey,
            context.ModelName,
            TranslationPromptBuilder.BuildScreenshotBatchStreamingPrompt(
                targetLanguage,
                context.CustomTranslationPrompt),
            context.EnableThinking);
        ValidateRequest(request);

        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("TranslationService", "screenshot_batch_stream.started", new
        {
            unit_count = units.Count,
            text_len = units.Sum(static unit => unit.SourceText.Length),
            requested_target_language = targetLanguage
        });
        var parser = new ScreenshotTranslationStreamParser(units.Select(static unit => unit.UnitId));
        ChatStreamingResult execution;
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(ScreenshotOverallTimeout);
        try
        {
            execution = await ExecuteChatStreamingAsync(
                request.ApiBaseUrl,
                request.ApiKey,
                BuildRequestBody(
                    request.ModelName,
                    [
                        new ChatCompletionMessage("system", request.SystemPrompt),
                        new ChatCompletionMessage("user", PromptInputContract.Wrap(request.Text))
                    ],
                    request.ApiBaseUrl,
                    request.EnableThinking,
                    stream: true),
                "screenshot batch streaming translation",
                streamEvent =>
                {
                    if (streamEvent.Kind != TranslationStreamEventKind.ContentDelta)
                        return;

                    foreach (var translated in parser.Append(streamEvent.Text))
                        onUnitCompleted(translated);
                },
                overallTimeout.Token,
                ScreenshotFirstChunkTimeout,
                ScreenshotIdleChunkTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && overallTimeout.IsCancellationRequested)
        {
            if (parser.IsComplete)
                return parser.Complete(units).MappedUnits;
            throw new ScreenshotTranslationTimeoutException(ScreenshotTranslationTimeoutKind.Overall);
        }
        catch (ScreenshotTranslationTimeoutException) when (parser.IsComplete)
        {
            // All expected units are already complete; a missing terminal
            // marker or post-content idle timeout does not invalidate them.
            return parser.Complete(units).MappedUnits;
        }
        catch (HttpRequestException) when (parser.IsComplete)
        {
            // Some compatible providers close the connection immediately after
            // the final unit without sending [DONE]. Content completeness wins.
            return parser.Complete(units).MappedUnits;
        }
        catch (KeyNotFoundException ex)
        {
            // A non-stream completion shape (message.content) is a provider
            // capability mismatch, not a transport failure; App may safely use
            // the non-stream structured batch fallback.
            throw new ScreenshotTranslationBatchFormatException("invalid_stream_response", ex);
        }

        var mapping = parser.Complete(units);
        if (!mapping.Accepted)
            throw new ScreenshotTranslationBatchFormatException(mapping.Reason);

        Logger.Info("TranslationService", "screenshot_batch_stream.completed", new
        {
            unit_count = units.Count,
            mapped_count = mapping.MappedCount,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            stream_chunk_count = execution.ChunkCount,
            first_chunk_ms = execution.FirstChunkMs
        });
        return mapping.MappedUnits;
    }

    private async Task<string> TranslateAsyncCore(
        string text,
        string targetLang,
        ContentType contentType,
        TranslationDirectionPreference directionPreference,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(
            text,
            contentType,
            TranslationRequestKind.Translation,
            CaptureRequestContext(targetLang),
            directionPreference);
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
            throw await ProviderHttpError.CreateExceptionAsync(
                "translation",
                response,
                cancellationToken).ConfigureAwait(false);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ExtractTranslation(responseBody);
    }

    private static void ValidateScreenshotBatchUnits(
        IReadOnlyList<ScreenshotTranslationUnit> units)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit);
            if (string.IsNullOrWhiteSpace(unit.UnitId) || !ids.Add(unit.UnitId))
                throw new ArgumentException("截图批量翻译单元 ID 必须非空且唯一。", nameof(units));
            if (string.IsNullOrWhiteSpace(unit.SourceText))
                throw new ArgumentException("截图批量翻译单元文本不能为空。", nameof(units));
        }
    }

    private static string BuildScreenshotBatchInput(
        IReadOnlyList<ScreenshotTranslationUnit> units)
    {
        var payload = new
        {
            units = units.Select(static unit => new
            {
                id = unit.UnitId,
                text = unit.SourceText
            })
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
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
        var userContent = request.Kind == TranslationRequestKind.Analysis ||
                          request.ContentType == ContentType.Translation
            ? request.Text
            : PromptInputContract.Wrap(request.Text);
        return BuildRequestBody(
            request.ModelName,
            [
                new ChatCompletionMessage("system", request.SystemPrompt),
                new ChatCompletionMessage("user", userContent)
            ],
            request.ApiBaseUrl,
            request.EnableThinking,
            stream);
    }

    private static Dictionary<string, object> BuildRequestBody(
        string modelName,
        IReadOnlyList<ChatCompletionMessage> messages,
        string apiBaseUrl,
        bool? enableThinking,
        bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = modelName,
            ["messages"] = messages,
            ["temperature"] = 0.3,
            ["stream"] = stream
        };

        ProviderRequestPolicy.Apply(body, apiBaseUrl, modelName, enableThinking);

        return body;
    }

    private async Task<ChatStreamingResult> ExecuteChatStreamingAsync(
        string apiBaseUrl,
        string apiKey,
        Dictionary<string, object> requestBody,
        string operation,
        Action<TranslationStreamEvent> onEvent,
        CancellationToken cancellationToken,
        TimeSpan? firstChunkTimeout = null,
        TimeSpan? idleChunkTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var streamStartedAt = Stopwatch.GetTimestamp();
        onEvent(new TranslationStreamEvent(TranslationStreamEventKind.Started));
        using var response = await SendAsync(
            apiBaseUrl,
            apiKey,
            requestBody,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await ProviderHttpError.CreateExceptionAsync(
                operation,
                response,
                cancellationToken).ConfigureAwait(false);
        }

        var fullResult = new StringBuilder();
        var chunkCount = 0;
        double? firstChunkMs = null;
        var maxChunkGapMs = 0.0;
        var totalChunkGapMs = 0.0;
        var stalledChunkCount = 0;
        long? previousChunkAt = null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            if (firstChunkTimeout is null && idleChunkTimeout is null)
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var timeout = chunkCount == 0 ? firstChunkTimeout!.Value : idleChunkTimeout!.Value;
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(timeout);
                try
                {
                    line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ScreenshotTranslationTimeoutException(
                        chunkCount == 0
                            ? ScreenshotTranslationTimeoutKind.FirstChunk
                            : ScreenshotTranslationTimeoutKind.Idle);
                }
            }
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
                if (TryGetText(delta, "reasoning_content", out var reasoning))
                    onEvent(new TranslationStreamEvent(TranslationStreamEventKind.ReasoningDelta, reasoning));
                else if (TryGetText(delta, "reasoning", out reasoning))
                    onEvent(new TranslationStreamEvent(TranslationStreamEventKind.ReasoningDelta, reasoning));

                if (!TryGetText(delta, "content", out var chunk))
                    continue;
                var chunkAt = Stopwatch.GetTimestamp();
                firstChunkMs ??= Stopwatch.GetElapsedTime(streamStartedAt, chunkAt).TotalMilliseconds;
                if (previousChunkAt is { } previous)
                {
                    var chunkGapMs = Stopwatch.GetElapsedTime(previous, chunkAt).TotalMilliseconds;
                    totalChunkGapMs += chunkGapMs;
                    maxChunkGapMs = Math.Max(maxChunkGapMs, chunkGapMs);
                    if (chunkGapMs >= StalledChunkGapThresholdMs)
                        stalledChunkCount++;
                }
                previousChunkAt = chunkAt;
                chunkCount++;
                fullResult.Append(chunk);
                onEvent(new TranslationStreamEvent(TranslationStreamEventKind.ContentDelta, chunk));
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
            chunkCount <= 1 ? 0 : totalChunkGapMs / (chunkCount - 1),
            maxChunkGapMs,
            stalledChunkCount);
    }

    private static bool TryGetText(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    private static string BuildAnalysisPrompt(string targetLang, TranslationRequestContext context)
    {
        return AnalysisPromptCatalog.Resolve(
            context.SelectedAnalysisPromptId,
            context.AnalysisPromptProfiles,
            targetLang);
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
            ["requested_target_language"] = request.RequestedTargetLanguage,
            ["effective_target_language"] = request.EffectiveTargetLanguage,
            ["fallback_used"] = request.FallbackUsed,
            ["direction_relation"] = request.Direction.Relation.ToString(),
            ["direction_confidence"] = request.Direction.Confidence.ToString(),
            ["direction_reason"] = request.Direction.Reason.ToString(),
            ["source_language_family"] = request.Direction.SourceLanguageFamily.ToString(),
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

    private static void EnsureInputLength(string text, int maxRunes, string operation)
    {
        var runeCount = text.EnumerateRunes().Count();
        if (runeCount > maxRunes)
            throw new InvalidOperationException($"{operation}内容过长，最多支持 {maxRunes} 个字符");
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

    private sealed record ChatStreamingResult(
        string Result,
        int ChunkCount,
        double? FirstChunkMs,
        double AverageChunkGapMs,
        double MaxChunkGapMs,
        int StalledChunkCount);

}
