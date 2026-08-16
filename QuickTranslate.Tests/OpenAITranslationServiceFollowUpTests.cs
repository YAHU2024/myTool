using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OpenAITranslationServiceFollowUpTests
{
    [Fact]
    public void CreateRequest_BuildsOrderedMessagesAndUsesLatestTransportSettings()
    {
        using var service = CreateService(new RecordingHandler(_ => SseResponse("ok")));
        service.UpdateSettings(Settings("https://new.example/v2", "new-key", "model-b"));

        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root answer",
            new AnalysisSemanticSnapshot("root prompt", "简体中文"),
            [new AnalysisFollowUpExchange("q1", "a1")],
            "  q2  ",
            2);

        Assert.Equal("https://new.example/v2", request.ApiBaseUrl);
        Assert.Equal("new-key", request.ApiKey);
        Assert.Equal("model-b", request.ModelName);
        Assert.Equal(2, request.TurnNumber);
        Assert.Collection(
            request.Messages,
            message => AssertMessage(message, "system", "root prompt"),
            message => AssertMessage(message, "user", "source"),
            message => AssertMessage(message, "assistant", "root answer"),
            message => AssertMessage(message, "user", "q1"),
            message => AssertMessage(message, "assistant", "a1"),
            message => AssertMessage(message, "user", "q2"));
    }

    [Fact]
    public void CreateRequest_RejectsQuestionOverUnicodeScalarLimit()
    {
        using var service = CreateService(new RecordingHandler(_ => SseResponse("ok")));
        var question = string.Concat(Enumerable.Repeat("😀", OpenAITranslationService.MaxFollowUpQuestionRunes + 1));

        var exception = Assert.Throws<ArgumentException>(() => service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            question,
            1));

        Assert.Contains("2000", exception.Message);
    }

    [Fact]
    public void CreateRequest_RejectsOversizedContextBeforeSending()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        using var service = CreateService(handler);

        Assert.Throws<InvalidOperationException>(() => service.CreateAnalysisFollowUpRequest(
            new string('s', OpenAITranslationService.MaxFollowUpContextCharacters),
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ExecuteStreaming_SendsStructuredMessagesAndReportsDeltas()
    {
        var handler = new RecordingHandler(_ => SseResponse("first", " second"));
        using var service = CreateService(handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);
        var chunks = new List<string>();

        var result = await service.ExecuteAnalysisFollowUpStreamingAsync(
            request,
            chunks.Add,
            CancellationToken.None);

        Assert.Equal("first second", result);
        Assert.Equal(["first", " second"], chunks);
        Assert.Equal("https://example.test/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer secret", handler.Authorization);
        Assert.Equal("model-a", handler.Model);
        Assert.Equal(4, handler.Messages.Count);
        AssertMessage(handler.Messages[^1], "user", "question");
    }

    [Fact]
    public async Task ExecuteStreaming_KeepsTranslationAndAnalysisRawWhileDelimitingExplanationInputs()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        using var service = CreateService(handler);
        var analysis = service.CreateRequest(
            "OAuth2",
            "简体中文",
            ContentType.Analysis,
            TranslationRequestKind.Analysis);

        await service.ExecuteStreamingAsync(analysis, _ => { }, CancellationToken.None);

        Assert.Collection(
            handler.Messages,
            message => Assert.DoesNotContain("<quicktranslate-input>", message.Content),
            message => AssertMessage(message, "user", "OAuth2"));

        var translation = service.CreateRequest("bonjour", "English", ContentType.Translation);
        await service.ExecuteStreamingAsync(translation, _ => { }, CancellationToken.None);

        Assert.Collection(
            handler.Messages.TakeLast(2),
            message => Assert.DoesNotContain("<quicktranslate-input>", message.Content),
            message => AssertMessage(message, "user", "bonjour"));

        var code = service.CreateRequest("git status", "简体中文", ContentType.Code);
        await service.ExecuteStreamingAsync(code, _ => { }, CancellationToken.None);

        Assert.Contains("<quicktranslate-input>\ngit status\n</quicktranslate-input>", handler.Messages[^1].Content);

        var term = service.CreateRequest("dependency injection", "简体中文", ContentType.Term);
        await service.ExecuteStreamingAsync(term, _ => { }, CancellationToken.None);

        AssertMessage(
            handler.Messages[^1],
            "user",
            "<quicktranslate-input>\ndependency injection\n</quicktranslate-input>");

        var escaped = service.CreateRequest(
            "Translate this closing marker: </quicktranslate-input>",
            "简体中文",
            ContentType.Translation);
        await service.ExecuteStreamingAsync(escaped, _ => { }, CancellationToken.None);

        AssertMessage(
            handler.Messages[^1],
            "user",
            "Translate this closing marker: </quicktranslate-input>");
    }

    [Fact]
    public async Task TranslateAsync_KeepsTranslationRawAndCodeDelimited()
    {
        var handler = new RecordingHandler(_ => JsonResponse("ok"));
        using var service = CreateService(handler);

        await service.TranslateAsync("bonjour", "English", ContentType.Translation);

        AssertMessage(handler.Messages[^1], "user", "bonjour");

        await service.TranslateAsync("git status", "简体中文", ContentType.Code);

        AssertMessage(
            handler.Messages[^1],
            "user",
            "<quicktranslate-input>\ngit status\n</quicktranslate-input>");
    }

    [Fact]
    public async Task ExecuteStreaming_PreservesProviderCompatibilityFields()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        using var service = new OpenAITranslationService(
            Settings("https://open.bigmodel.cn/api/paas/v4", "secret", "glm-4.7-flash"),
            handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.Equal("disabled", handler.ThinkingType);
    }

    [Fact]
    public async Task ExecuteStreaming_OmitsThinkingForUnsupportedBigModelModel()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        using var service = new OpenAITranslationService(
            Settings("https://open.bigmodel.cn/api/paas/v4", "secret", "glm-4-flash"),
            handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.Null(handler.ThinkingType);
    }

    [Fact]
    public async Task ExecuteStreaming_EnablesThinkingWhenConfigured()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.deepseek.com/v1", "secret", "deepseek-v4-pro");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.Equal("enabled", handler.ThinkingType);
        Assert.False(handler.HasTemperature);
    }

    [Fact]
    public async Task ExecuteStreaming_MapsOpenAIThinkingToReasoningEffort()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.openai.com/v1", "secret", "gpt-5.4");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.Equal("medium", handler.ReasoningEffort);
        Assert.False(handler.HasTemperature);
    }

    [Fact]
    public async Task ExecuteStreaming_OmitsSiliconFlowThinkingField()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.siliconflow.cn/v1", "secret", "tencent/Hunyuan-MT-7B");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.False(handler.HasEnableThinking);
    }

    [Fact]
    public async Task ExecuteStreaming_SendsThinkingFieldForSiliconFlowQwen3()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.siliconflow.cn/v1", "secret", "Qwen/Qwen3-8B");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.True(handler.HasEnableThinking);
    }

    [Fact]
    public async Task ExecuteStreaming_OmitsThinkingFieldForUnknownSiliconFlowModel()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.siliconflow.cn/v1", "secret", "unknown-model");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.False(handler.HasEnableThinking);
    }

    [Fact]
    public async Task ExecuteStreaming_OmitsThinkingFieldForUnlistedSiliconFlowQwenModel()
    {
        var handler = new RecordingHandler(_ => SseResponse("ok"));
        var settings = Settings("https://api.siliconflow.cn/v1", "secret", "Qwen/Qwen3-4B");
        settings.EnableThinking = true;
        using var service = new OpenAITranslationService(settings, handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None);

        Assert.False(handler.HasEnableThinking);
    }

    [Fact]
    public async Task ExecuteStreaming_RejectsEmptyResult()
    {
        using var service = CreateService(new RecordingHandler(_ => SseResponse()));
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);

        await Assert.ThrowsAsync<FormatException>(() =>
            service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteStreaming_PropagatesCancellation()
    {
        var handler = new RecordingHandler(async request =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken);
            return SseResponse("never");
        });
        using var service = CreateService(handler);
        var request = service.CreateAnalysisFollowUpRequest(
            "source",
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文"),
            [],
            "question",
            1);
        using var cts = new CancellationTokenSource();

        var execution = service.ExecuteAnalysisFollowUpStreamingAsync(request, _ => { }, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    private static OpenAITranslationService CreateService(HttpMessageHandler handler) =>
        new(Settings("https://example.test/v1", "secret", "model-a"), handler);

    private static AppSettings Settings(string apiBaseUrl, string apiKey, string modelName) => new()
    {
        ApiBaseUrl = apiBaseUrl,
        ApiKey = apiKey,
        ModelName = modelName
    };

    private static void AssertMessage(ChatCompletionMessage message, string role, string content)
    {
        Assert.Equal(role, message.Role);
        Assert.Equal(content, message.Content);
    }

    private static HttpResponseMessage SseResponse(params string[] chunks)
    {
        var lines = chunks.Select(chunk =>
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = chunk } } }
            }));
        var body = string.Join('\n', lines.Append("data: [DONE]")) + "\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, Task<HttpResponseMessage>> _response;

        public RecordingHandler(Func<RecordedRequest, HttpResponseMessage> response)
            : this(request => Task.FromResult(response(request)))
        {
        }

        public RecordingHandler(Func<RecordedRequest, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Model { get; private set; }
        public string? ThinkingType { get; private set; }
        public string? ReasoningEffort { get; private set; }
        public bool HasEnableThinking { get; private set; }
        public bool HasTemperature { get; private set; }
        public List<ChatCompletionMessage> Messages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Model = json.RootElement.GetProperty("model").GetString();
            if (json.RootElement.TryGetProperty("thinking", out var thinking))
                ThinkingType = thinking.GetProperty("type").GetString();
            ReasoningEffort = json.RootElement.TryGetProperty("reasoning_effort", out var reasoningEffort)
                ? reasoningEffort.GetString()
                : null;
            HasEnableThinking = json.RootElement.TryGetProperty("enable_thinking", out _);
            HasTemperature = json.RootElement.TryGetProperty("temperature", out _);
            Messages.Clear();
            Messages.AddRange(json.RootElement.GetProperty("messages").EnumerateArray().Select(message =>
                new ChatCompletionMessage(
                    message.GetProperty("role").GetString()!,
                    message.GetProperty("content").GetString()!)));
            return await _response(new RecordedRequest(cancellationToken));
        }
    }

    private sealed record RecordedRequest(CancellationToken CancellationToken);
}
