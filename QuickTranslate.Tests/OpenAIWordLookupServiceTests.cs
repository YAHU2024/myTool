using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OpenAIWordLookupServiceTests
{
    [Fact]
    public void ParseResult_ParsesFencedCompleteResultAndLabelsAiSource()
    {
        var json = """
            ```json
            {"status":"found","headword":"run","pronunciations":[{"region":"US","phonetic":"/rʌn/"}],"senses":[{"part_of_speech":"verb","definition":"跑","english_definition":"move quickly"}],"examples":[{"sentence":"I run.","translation":"我跑步。"}],"collocations":["run fast"]}
            ```
            """;

        var result = OpenAIWordLookupService.ParseResult(json, "model-x");

        Assert.Equal("run", result.Headword);
        Assert.Single(result.Pronunciations);
        Assert.Single(result.Senses);
        Assert.Equal("动词", result.Senses[0].PartOfSpeech);
        Assert.Single(result.Examples);
        Assert.Single(result.Collocations);
        Assert.Equal(WordLookupSourceKind.AiGenerated, result.Source.Kind);
        Assert.Equal("AI 释义 · model-x", result.Source.DisplayName);
    }

    [Theory]
    [InlineData("noun", "名词")]
    [InlineData("n.", "名词")]
    [InlineData("adjective", "形容词")]
    [InlineData("adv.", "副词")]
    [InlineData("r", "副词")]
    [InlineData("vt.", "动词")]
    [InlineData("phrasal verb", "短语动词")]
    [InlineData("副词", "副词")]
    [InlineData("unexpected-provider-label", "其他")]
    public void PartOfSpeechNormalizer_ReturnsChineseLabel(string input, string expected)
    {
        Assert.Equal(expected, WordPartOfSpeechNormalizer.ToDisplayLabel(input));
    }

    [Fact]
    public void ParseResult_AllowsMissingOptionalCollections()
    {
        var result = OpenAIWordLookupService.ParseResult(
            "{\"status\":\"found\",\"headword\":\"run\",\"senses\":[{\"definition\":\"跑\"}]}",
            "model-x");

        Assert.Empty(result.Pronunciations);
        Assert.Empty(result.Examples);
        Assert.Empty(result.Collocations);
    }

    [Fact]
    public void ParseResult_ThrowsNotFoundForExplicitStatus()
    {
        Assert.Throws<WordLookupNotFoundException>(() =>
            OpenAIWordLookupService.ParseResult("{\"status\":\"not_found\"}", "model-x"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"status\":\"found\",\"headword\":\"run\",\"senses\":[]}")]
    [InlineData("{\"status\":\"found\",\"headword\":\"\",\"senses\":[{\"definition\":\"x\"}]}")]
    public void ParseResult_RejectsMalformedOrIncompleteResults(string content)
    {
        Assert.Throws<WordLookupFormatException>(() =>
            OpenAIWordLookupService.ParseResult(content, "model-x"));
    }

    [Fact]
    public void ParseResult_RejectsExcessCollectionItems()
    {
        var senses = string.Join(',', Enumerable.Repeat("{\"definition\":\"x\"}", 7));

        Assert.Throws<WordLookupFormatException>(() =>
            OpenAIWordLookupService.ParseResult(
                $"{{\"status\":\"found\",\"headword\":\"run\",\"senses\":[{senses}]}}",
                "model-x"));
    }

    [Fact]
    public async Task LookupAsync_SendsExpectedRequestWithoutLeakingIntoException()
    {
        var handler = new RecordingHandler(_ => JsonResponse(FoundEnvelope("run")));
        using var service = CreateService(handler);

        var result = await service.LookupAsync(
            new WordLookupRequest(" run ", "简体中文"),
            CancellationToken.None);

        Assert.Equal("run", result.Headword);
        Assert.Equal("Bearer secret", handler.Authorization);
        Assert.Equal("https://example.test/v1/chat/completions", handler.RequestUri);
        Assert.Equal("model-a", handler.Model);
        Assert.Equal("run", handler.UserContent);
    }

    [Fact]
    public async Task LookupAsync_UsesOneSettingsSnapshotPerRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async request =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
            return JsonResponse(FoundEnvelope("result"));
        });
        using var service = CreateService(handler);

        var first = service.LookupAsync(new WordLookupRequest("first", ""), CancellationToken.None);
        await firstStarted.Task;
        service.UpdateSettings(new WordLookupProviderSettings(
            "https://new.example/v2", "new-secret", "model-b", "English"));
        releaseFirst.TrySetResult();
        await first;

        Assert.Equal("https://example.test/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer secret", handler.Authorization);
        Assert.Equal("model-a", handler.Model);

        await service.LookupAsync(new WordLookupRequest("second", ""), CancellationToken.None);
        Assert.Equal("https://new.example/v2/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer new-secret", handler.Authorization);
        Assert.Equal("model-b", handler.Model);
    }

    [Fact]
    public async Task LookupAsync_PropagatesCancellation()
    {
        var handler = new RecordingHandler(async request =>
        {
            await Task.Delay(Timeout.Infinite, request.CancellationToken);
            return JsonResponse(FoundEnvelope("never"));
        });
        using var service = CreateService(handler);
        using var cts = new CancellationTokenSource();

        var task = service.LookupAsync(new WordLookupRequest("run", ""), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task LookupAsync_RejectsRemotePlaintextBeforeSending()
    {
        var handler = new RecordingHandler(_ => JsonResponse(FoundEnvelope("run")));
        using var service = new OpenAIWordLookupService(
            new WordLookupProviderSettings("http://example.test/v1", "secret", "model-a", "中文"),
            handler);

        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupAsync(
            new WordLookupRequest("run", ""), CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EnrichAsync_TranslatesMissingFields_AndPreservesLocalEnglish()
    {
        var enrichment = """
            {"senses":[{"index":0,"definition":"快速移动"}],"examples":[{"index":0,"translation":"他们每天跑步。"}]}
            """;
        var handler = new RecordingHandler(_ => JsonResponse(CompletionEnvelope(enrichment)));
        using var service = CreateService(handler);
        var local = LocalResult();

        var result = await service.EnrichAsync(
            new WordLookupRequest("run", "简体中文"),
            local,
            CancellationToken.None);

        Assert.Equal("快速移动", result.Senses[0].Definition);
        Assert.Equal("move quickly", result.Senses[0].EnglishDefinition);
        Assert.Equal("They run daily.", result.Examples[0].Sentence);
        Assert.Equal("他们每天跑步。", result.Examples[0].Translation);
        Assert.Equal(WordLookupSourceKind.Hybrid, result.Source.Kind);
        Assert.Contains("AI 补全", result.Source.DisplayName);
        Assert.Contains("They run daily.", handler.UserContent);
        Assert.DoesNotContain("快速移动", handler.UserContent);
        using var payload = JsonDocument.Parse(handler.UserContent!);
        Assert.False(payload.RootElement.TryGetProperty("headword", out _));
        Assert.True(payload.RootElement.TryGetProperty("senses", out _));
        Assert.True(payload.RootElement.TryGetProperty("examples", out _));
    }

    [Fact]
    public void ApplyEnrichment_RejectsPartialResponse()
    {
        var local = LocalResult();

        Assert.Throws<WordLookupFormatException>(() =>
            OpenAIWordLookupService.ApplyEnrichment(
                "{\"senses\":[{\"index\":0,\"definition\":\"快速移动\"}],\"examples\":[]}",
                local,
                "model-a"));
    }

    private static OpenAIWordLookupService CreateService(HttpMessageHandler handler) => new(
        new WordLookupProviderSettings(
            "https://example.test/v1", "secret", "model-a", "简体中文"),
        handler);

    private static string FoundEnvelope(string headword)
    {
        var content = $"{{\"status\":\"found\",\"headword\":\"{headword}\",\"senses\":[{{\"definition\":\"释义\"}}]}}";
        return CompletionEnvelope(content);
    }

    private static string CompletionEnvelope(string content)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
    }

    private static WordLookupResult LocalResult() => new(
        "run",
        Array.Empty<WordPronunciation>(),
        [new WordSense("动词", string.Empty, "move quickly")],
        [new WordExample("They run daily.", string.Empty)],
        Array.Empty<string>(),
        new WordLookupSource(
            "ecdict-oewn-local",
            "本地词典 · ECDICT + OEWN",
            WordLookupSourceKind.Dictionary));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

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
        public string? UserContent { get; private set; }

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
            UserContent = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            return await _response(new RecordedRequest(cancellationToken));
        }
    }

    private sealed record RecordedRequest(CancellationToken CancellationToken);
}
