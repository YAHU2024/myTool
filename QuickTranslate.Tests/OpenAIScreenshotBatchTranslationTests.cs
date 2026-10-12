using System.Net;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_AcceptsCompleteUnitsWithoutDoneMarker()
    {
        var handler = new StreamingBatchHandler(
            SseWithoutDone(
                "{\"id\":\"u0001\",\"translation\":\"第一\"}",
                "{\"id\":\"u0002\",\"translation\":\"第二\"}"));
        using var service = CreateService(handler);

        var result = await service.TranslateScreenshotBatchStreamingAsync(
            Units(),
            "简体中文",
            _ => { });

        Assert.Equal(new[] { "u0001", "u0002" }, result.Select(unit => unit.UnitId));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TranslateScreenshotBatchStreamingAsync_PropagatesProviderStatusCode(
        HttpStatusCode statusCode)
    {
        using var service = CreateService(new StatusHandler(statusCode));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                _ => { },
                CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
        var expectedKind = ScreenshotTranslationFailureClassifier.Classify(
            exception,
            "translation",
            cancellationRequested: false);
        Assert.Equal(
            statusCode switch
            {
                HttpStatusCode.Unauthorized => ScreenshotTranslationFailureKind.ProviderUnauthorized,
                HttpStatusCode.TooManyRequests => ScreenshotTranslationFailureKind.ProviderQuota,
                _ => ScreenshotTranslationFailureKind.ProviderServer
            },
            expectedKind);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_ClassifiesTransportFailureWithoutRetrying()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection failed"));
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(Units(), "简体中文", _ => { }));

        Assert.Equal(ScreenshotTranslationFailureKind.ProviderTransport,
            ScreenshotTranslationFailureClassifier.Classify(exception, "translation", false));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_ReportsFirstChunkTimeout()
    {
        var handler = new DelayedStreamingHandler(
            new[] { SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}") },
            new[] { TimeSpan.FromMilliseconds(150) });
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                _ => { },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.FirstChunk, exception.TimeoutKind);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_ReportsConnectTimeoutBeforeHeaders()
    {
        using var service = CreateService(new DelayedResponseHandler(TimeSpan.FromMilliseconds(150)));

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                _ => { },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.Connect, exception.TimeoutKind);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_ReportsIdleTimeoutAfterPartialCompletion()
    {
        var handler = new DelayedStreamingHandler(
            new[]
            {
                SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}"),
                SseLine("{\"id\":\"u0002\",\"translation\":\"第二\"}") + "data: [DONE]\n"
            },
            new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(150) });
        using var service = CreateService(handler);
        var received = new List<TranslatedTextUnit>();

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                received.Add,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.Idle, exception.TimeoutKind);
        Assert.Equal(new[] { "u0001" }, received.Select(unit => unit.UnitId));
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_KeepAliveLinesDoNotExtendFirstChunkDeadline()
    {
        var handler = new DelayedStreamingHandler(
            new[]
            {
                ": keep-alive\n",
                ": keep-alive\n",
                SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}")
            },
            new[]
            {
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)
            });
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                _ => { },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.FirstChunk, exception.TimeoutKind);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_KeepAliveLinesDoNotExtendIdleDeadline()
    {
        var handler = new DelayedStreamingHandler(
            new[]
            {
                SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}"),
                ": keep-alive\n",
                ": keep-alive\n",
                SseLine("{\"id\":\"u0002\",\"translation\":\"第二\"}")
            },
            new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10)
            });
        using var service = CreateService(handler);
        var received = new List<TranslatedTextUnit>();

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                received.Add,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.Idle, exception.TimeoutKind);
        Assert.Equal(new[] { "u0001" }, received.Select(unit => unit.UnitId));
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_ReportsOverallTimeoutWhenOverallBudgetExpiresFirst()
    {
        var handler = new DelayedStreamingHandler(
            new[] { SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}") },
            new[] { TimeSpan.FromMilliseconds(150) });
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationTimeoutException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(
                Units(),
                "简体中文",
                _ => { },
                CancellationToken.None,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(20)));

        Assert.Equal(ScreenshotTranslationTimeoutKind.Overall, exception.TimeoutKind);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_PreservesPartialUnitsAndRejectsMissingUnits()
    {
        var handler = new StreamingBatchHandler(
            Sse(
                "{\"id\":\"u0001\",\"translation\":\"第一\"}"));
        using var service = CreateService(handler);
        var received = new List<TranslatedTextUnit>();

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationBatchFormatException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(Units(), "简体中文", received.Add));

        Assert.Equal("missing_id", exception.Reason);
        Assert.Equal(new[] { "u0001" }, received.Select(unit => unit.UnitId));
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_RejectsMalformedProviderEnvelope()
    {
        var handler = new StreamingBatchHandler(
            RawSse("{\"choices\":[{}]}"));
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationBatchFormatException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(Units(), "简体中文", _ => { }));

        Assert.Equal("invalid_stream_response", exception.Reason);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_RejectsMalformedPerUnitPayload()
    {
        var handler = new StreamingBatchHandler(
            Sse("{\"id\":\"u0001\",\"translation\":42}"));
        using var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ScreenshotTranslationBatchFormatException>(() =>
            service.TranslateScreenshotBatchStreamingAsync(Units(), "简体中文", _ => { }));

        Assert.Equal("invalid_unit", exception.Reason);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_CanRequestOnlyMissingUnitsForRetry()
    {
        var handler = new StreamingBatchHandler(
            Sse("{\"id\":\"u0002\",\"translation\":\"第二\"}"));
        using var service = CreateService(handler);

        var result = await service.TranslateScreenshotBatchStreamingAsync(
            new[] { Units()[1] },
            "简体中文",
            _ => { });

        Assert.Equal(new[] { "u0002" }, result.Select(unit => unit.UnitId));
        Assert.Contains("\"id\":\"u0002\"", handler.UserContent);
        Assert.DoesNotContain("\"id\":\"u0001\"", handler.UserContent);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranslateScreenshotBatchStreamingAsync_PropagatesUserCancellationAfterPartialUnit()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new DelayedStreamingHandler(
            new[]
            {
                SseLine("{\"id\":\"u0001\",\"translation\":\"第一\"}"),
                SseLine("{\"id\":\"u0002\",\"translation\":\"第二\"}")
            },
            new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(150) });
        using var service = CreateService(handler);
        var received = new List<TranslatedTextUnit>();

        var task = service.TranslateScreenshotBatchStreamingAsync(
            Units(),
            "简体中文",
            unit =>
            {
                received.Add(unit);
                cancellation.Cancel();
            },
            cancellation.Token,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(new[] { "u0001" }, received.Select(unit => unit.UnitId));
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

        public string UserContent { get; private set; } = string.Empty;

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Stream = document.RootElement.GetProperty("stream").GetBoolean();
            var messages = document.RootElement.GetProperty("messages");
            SystemPrompt = messages[0].GetProperty("content").GetString() ?? string.Empty;
            UserContent = messages[1].GetProperty("content").GetString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StatusHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{\"error\":{\"message\":\"provider failure\"}}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }

    private sealed class DelayedResponseHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayedResponseHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Sse(
                    "{\"id\":\"u0001\",\"translation\":\"第一\"}",
                    "{\"id\":\"u0002\",\"translation\":\"第二\"}"),
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }
    }

    private sealed class DelayedStreamingHandler : HttpMessageHandler
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;

        public DelayedStreamingHandler(IReadOnlyList<string> chunks, IReadOnlyList<TimeSpan> delays)
        {
            if (chunks.Count != delays.Count)
                throw new ArgumentException("Chunk and delay counts must match.");

            _chunks = chunks.Select(Encoding.UTF8.GetBytes).ToArray();
            _delays = delays.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(new DelayedChunkStream(_chunks, _delays));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class DelayedChunkStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;
        private int _chunkIndex;
        private bool _disposed;

        public DelayedChunkStream(byte[][] chunks, TimeSpan[] delays)
        {
            _chunks = chunks;
            _delays = delays;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _chunks.Sum(static chunk => chunk.LongLength);
        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_disposed || _chunkIndex >= _chunks.Length)
                return 0;

            var delay = _delays[_chunkIndex];
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var chunk = _chunks[_chunkIndex++];
            chunk.AsSpan().CopyTo(buffer.Span);
            Position += chunk.Length;
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private static string SseWithoutDone(params string[] fragments)
    {
        var lines = fragments.Select(fragment =>
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = fragment } } }
            }));
        return string.Join("\n", lines) + "\n\n";
    }

    private static string RawSse(params string[] payloads) =>
        string.Join("\n", payloads.Select(payload => "data: " + payload).Append("data: [DONE]")) + "\n\n";

    private static string SseLine(string json) => $"data: {JsonSerializer.Serialize(new
    {
        choices = new[] { new { delta = new { content = json } } }
    })}\n";
}
