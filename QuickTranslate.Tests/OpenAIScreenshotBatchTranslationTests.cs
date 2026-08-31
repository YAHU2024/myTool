using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OpenAIScreenshotBatchTranslationTests
{
    [Fact]
    public async Task TranslateScreenshotBatchAsync_SendsOneStructuredRequestAndMapsById()
    {
        var handler = new BatchHandler(
            ProviderResponse("{\"units\":[{\"id\":\"u0002\",\"translation\":\"第二\"},{\"id\":\"u0001\",\"translation\":\"第一\"}]}")
        );
        using var service = CreateService(handler);

        var result = await service.TranslateScreenshotBatchAsync(Units(), "简体中文");

        Assert.Equal(new[] { "u0001", "u0002" }, result.Select(unit => unit.UnitId));
        Assert.Equal(new[] { "第一", "第二" }, result.Select(unit => unit.Translation));
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("Screenshot translation policy (mandatory)", handler.SystemPrompt);
        Assert.Contains("Never switch to a fallback language", handler.SystemPrompt);
        Assert.Contains("Translate every natural-language text segment into 简体中文", handler.SystemPrompt);
        Assert.Contains("Prefer concise wording in 简体中文.", handler.SystemPrompt);
        Assert.Contains("<quicktranslate-input>", handler.UserContent);
        Assert.Contains("\"id\":\"u0001\"", handler.UserContent);
        Assert.Contains("\"text\":\"Hello world\"", handler.UserContent);
        Assert.False(handler.Stream);
    }

    [Fact]
    public async Task TranslateScreenshotBatchAsync_RejectsResponseWithIncompleteIds()
    {
        var handler = new BatchHandler(
            ProviderResponse("{\"units\":[{\"id\":\"u0001\",\"translation\":\"第一\"}]}")
        );
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationBatchFormatException>(() =>
            service.TranslateScreenshotBatchAsync(Units(), "简体中文"));

        Assert.Equal("missing_id", exception.Reason);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranslateScreenshotBatchAsync_DoesNotCallProviderForEmptyUnits()
    {
        var handler = new BatchHandler(ProviderResponse("{}"));
        using var service = CreateService(handler);

        var result = await service.TranslateScreenshotBatchAsync(
            Array.Empty<ScreenshotTranslationUnit>(),
            "简体中文");

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranslateScreenshotBatchAsync_RejectsDuplicateUnitIdsBeforeProviderCall()
    {
        var handler = new BatchHandler(ProviderResponse("{}"));
        using var service = CreateService(handler);
        var duplicate = new[]
        {
            new ScreenshotTranslationUnit("u0001", "one", Array.Empty<OcrTextBlock>(), new OcrBounds(0, 0, 10, 10)),
            new ScreenshotTranslationUnit("u0001", "two", Array.Empty<OcrTextBlock>(), new OcrBounds(0, 20, 10, 10))
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TranslateScreenshotBatchAsync(duplicate, "简体中文"));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_PublishesCompletedUnitsFromSplitSseContent()
    {
        var handler = new StreamingBatchHandler(
            Sse(
                "{\"id\":\"u0002\",\"trans",
                "lation\":\"第二\"}\n",
                "{\"id\":\"u0001\",\"translation\":\"第一\"}"));
        using var service = CreateService(handler);
        var received = new List<TranslatedTextUnit>();

        var result = await service.TranslateScreenshotBatchStreamingAsync(
            Units(),
            "简体中文",
            received.Add);

        Assert.Equal(new[] { "u0002", "u0001" }, received.Select(unit => unit.UnitId));
        Assert.Equal(new[] { "u0001", "u0002" }, result.Select(unit => unit.UnitId));
        Assert.True(handler.Stream);
        Assert.Contains("one complete compact JSON object per translated unit", handler.SystemPrompt);
    }

    private static OpenAITranslationService CreateService(HttpMessageHandler handler) =>
        new(
            new AppSettings
            {
                ApiBaseUrl = "https://example.test/v1",
                ApiKey = "key",
                ModelName = "model",
                AutoDetectLanguage = true,
                FallbackLanguage = "English",
                CustomTranslationPrompt = "Prefer concise wording in {targetLang}."
            },
            handler);

    private static IReadOnlyList<ScreenshotTranslationUnit> Units() => new[]
    {
        new ScreenshotTranslationUnit(
            "u0001",
            "Hello world",
            Array.Empty<OcrTextBlock>(),
            new OcrBounds(0, 0, 100, 20)),
        new ScreenshotTranslationUnit(
            "u0002",
            "Settings",
            Array.Empty<OcrTextBlock>(),
            new OcrBounds(0, 30, 100, 20))
    };

    private static string ProviderResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });

    private sealed class BatchHandler : HttpMessageHandler
    {
        private readonly string _response;

        public BatchHandler(string response) => _response = response;

        public int CallCount { get; private set; }

        public string SystemPrompt { get; private set; } = string.Empty;

        public string UserContent { get; private set; } = string.Empty;

        public bool Stream { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var messages = document.RootElement.GetProperty("messages");
            SystemPrompt = messages[0].GetProperty("content").GetString() ?? string.Empty;
            UserContent = messages[1].GetProperty("content").GetString() ?? string.Empty;
            Stream = document.RootElement.GetProperty("stream").GetBoolean();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StreamingBatchHandler : HttpMessageHandler
    {
        private readonly string _response;

        public StreamingBatchHandler(string response) => _response = response;

        public bool Stream { get; private set; }

        public string SystemPrompt { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Stream = document.RootElement.GetProperty("stream").GetBoolean();
            SystemPrompt = document.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static string Sse(params string[] fragments)
    {
        var lines = fragments.Select(fragment =>
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = fragment } } }
            }));
        return string.Join("\n", lines.Append("data: [DONE]")) + "\n\n";
    }
}
