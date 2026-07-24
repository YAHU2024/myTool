using System.Net.WebSockets;
using System.Text;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class EdgeTtsClientTests
{
    [Fact]
    public void GenerateSecMsGec_IsStableUpperHexForFixedTime()
    {
        var utc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = EdgeTtsClient.GenerateSecMsGec(utc);
        var b = EdgeTtsClient.GenerateSecMsGec(utc);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a.ToUpperInvariant(), a);
    }

    [Fact]
    public void BuildWebSocketUri_ContainsTokenAndGec()
    {
        var uri = EdgeTtsClient.BuildWebSocketUri("abc", "DEADBEEF");
        Assert.Contains("TrustedClientToken=", uri.Query);
        Assert.Contains("ConnectionId=abc", uri.Query);
        Assert.Contains("Sec-MS-GEC=DEADBEEF", uri.Query);
        Assert.Contains("Sec-MS-GEC-Version=", uri.Query);
    }

    [Fact]
    public void EscapeAndSplit_PreserveEntities()
    {
        var escaped = TtsTextSelector.EscapeSsml("a & b < c");
        Assert.Contains("&amp;", escaped);
        Assert.Contains("&lt;", escaped);
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(escaped, 8);
        Assert.All(parts, part => Assert.False(part.Contains('&') && !part.Contains(';')));
    }

    [Fact]
    public void TryExtractAudioFromBinaryMessage_ReadsPayload()
    {
        var headers = Encoding.UTF8.GetBytes("Content-Type:audio/mpeg\r\nPath:audio\r\n");
        var audio = Encoding.ASCII.GetBytes("ID3AUDIO");
        var message = new byte[2 + headers.Length + 2 + audio.Length];
        message[0] = (byte)(headers.Length >> 8);
        message[1] = (byte)(headers.Length & 0xFF);
        Buffer.BlockCopy(headers, 0, message, 2, headers.Length);
        message[2 + headers.Length] = (byte)'\r';
        message[2 + headers.Length + 1] = (byte)'\n';
        Buffer.BlockCopy(audio, 0, message, 2 + headers.Length + 2, audio.Length);

        Assert.True(EdgeTtsClient.TryExtractAudioFromBinaryMessage(message, out var extracted));
        Assert.Equal(audio, extracted);
    }

    [Fact]
    public async Task SynthesizeAsync_UsesFakeSessionAndSupportsCancel()
    {
        var audioPayload = BuildAudioBinary("FAKEMP3");
        var session = new FakeSession(new[]
        {
            (true, audioPayload),
            (false, Encoding.UTF8.GetBytes("Path:turn.end\r\n"))
        });

        var client = new EdgeTtsClient((_, _, _) => Task.FromResult<IEdgeTtsSession>(session));
        var bytes = await client.SynthesizeAsync("hello", TtsTextSelector.VoiceJenny, 1.0, CancellationToken.None);
        Assert.Equal(Encoding.ASCII.GetBytes("FAKEMP3"), bytes);
        Assert.Equal(2, session.SentMessages.Count);
        Assert.Contains("Path:speech.config", session.SentMessages[0]);
        Assert.Contains("Path:ssml", session.SentMessages[1]);
        Assert.Contains("&lt;", TtsTextSelector.EscapeSsml("<"));
    }

    [Fact]
    public async Task SynthesizeAsync_CancelBeforeReceive_Throws()
    {
        var session = new FakeSession(Array.Empty<(bool, byte[])>(), delayReceive: true);
        var client = new EdgeTtsClient((_, _, _) => Task.FromResult<IEdgeTtsSession>(session));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SynthesizeAsync("hello", TtsTextSelector.VoiceJenny, 1.0, cts.Token));
    }

    private static byte[] BuildAudioBinary(string audioText)
    {
        var headers = Encoding.UTF8.GetBytes("Path:audio\r\n");
        var audio = Encoding.ASCII.GetBytes(audioText);
        var message = new byte[2 + headers.Length + 2 + audio.Length];
        message[0] = (byte)(headers.Length >> 8);
        message[1] = (byte)(headers.Length & 0xFF);
        Buffer.BlockCopy(headers, 0, message, 2, headers.Length);
        message[2 + headers.Length] = (byte)'\r';
        message[2 + headers.Length + 1] = (byte)'\n';
        Buffer.BlockCopy(audio, 0, message, 2 + headers.Length + 2, audio.Length);
        return message;
    }

    private sealed class FakeSession : IEdgeTtsSession
    {
        private readonly Queue<(bool IsBinary, byte[] Payload)> _messages;
        private readonly bool _delayReceive;

        public FakeSession(IEnumerable<(bool, byte[])> messages, bool delayReceive = false)
        {
            _messages = new Queue<(bool, byte[])>(messages);
            _delayReceive = delayReceive;
        }

        public List<string> SentMessages { get; } = new();

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public async Task<(bool IsBinary, byte[] Payload)?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_delayReceive)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_messages.Count == 0)
                return null;
            return _messages.Dequeue();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
