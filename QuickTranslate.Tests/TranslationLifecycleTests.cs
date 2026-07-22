using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class TranslationLifecycleTests
{
    [Fact]
    public void LatestRequestCoordinator_CancelsAndInvalidatesOlderScope()
    {
        var coordinator = new LatestRequestCoordinator();
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));

        coordinator.Complete(first);
        Assert.True(coordinator.IsCurrent(second));

        coordinator.Complete(second);
        Assert.False(coordinator.IsCurrent(second));
    }

    [Fact]
    public void TranslationRequest_SnapshotsPromptAndModel()
    {
        var settings = new AppSettings
        {
            ApiKey = "key",
            ModelName = "model-a",
            CustomTranslationPrompt = "Use {targetLang}."
        };
        using var service = new OpenAITranslationService(settings);

        var first = service.CreateRequest("hello", "English", ContentType.Translation);
        settings.ModelName = "model-b";
        settings.CustomTranslationPrompt = "Changed {targetLang}.";

        Assert.Equal("model-a", first.ModelName);
        Assert.Equal("Use English.", first.SystemPrompt);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CancellationStopsSend()
    {
        var handler = new CancellationHandler();
        using var service = new OpenAITranslationService(
            new AppSettings { ApiKey = "key" },
            handler);
        var request = service.CreateRequest("hello", "简体中文", ContentType.Translation);
        using var cts = new CancellationTokenSource();

        var execution = service.ExecuteStreamingAsync(request, _ => { }, cts.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CancellationStopsStreamRead()
    {
        var stream = new BlockingReadStream();
        var handler = new ResponseHandler(stream);
        using var service = new OpenAITranslationService(
            new AppSettings { ApiKey = "key" },
            handler);
        var request = service.CreateRequest("hello", "简体中文", ContentType.Translation);
        using var cts = new CancellationTokenSource();

        var execution = service.ExecuteStreamingAsync(request, _ => { }, cts.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CombinesSseChunks()
    {
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var handler = new ResponseHandler(stream);
        using var service = new OpenAITranslationService(
            new AppSettings { ApiKey = "key" },
            handler);
        var request = service.CreateRequest("hello", "简体中文", ContentType.Translation);
        var chunks = new List<string>();

        var result = await service.ExecuteStreamingAsync(request, chunks.Add);

        Assert.Equal("你好", result);
        Assert.Equal(new[] { "你", "你好" }, chunks);
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Stream _stream;

        public ResponseHandler(Stream stream) => _stream = stream;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(_stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
