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
        Assert.Contains("xml:lang='en-US'", session.SentMessages[1]);
        Assert.Contains("&lt;", TtsTextSelector.EscapeSsml("<"));
    }

    [Fact]
    public async Task SynthesizeAsync_ChineseVoice_UsesZhCnSsmlLang()
    {
        var audioPayload = BuildAudioBinary("FAKEMP3");
        var session = new FakeSession(new[]
        {
            (true, audioPayload),
            (false, Encoding.UTF8.GetBytes("Path:turn.end\r\n"))
        });

        var client = new EdgeTtsClient((_, _, _) => Task.FromResult<IEdgeTtsSession>(session));
        await client.SynthesizeAsync("你好", TtsTextSelector.VoiceXiaoxiao, 1.0, CancellationToken.None);
        Assert.Contains("xml:lang='zh-CN'", session.SentMessages[1]);
        Assert.Contains(TtsTextSelector.VoiceXiaoxiao, session.SentMessages[1]);
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyAudio_ThrowsInvalidOperation()
    {
        var session = new FakeSession(new[]
        {
            (false, Encoding.UTF8.GetBytes("Path:turn.end\r\n"))
        });
        var client = new EdgeTtsClient((_, _, _) => Task.FromResult<IEdgeTtsSession>(session));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SynthesizeAsync("hello", TtsTextSelector.VoiceJenny, 1.0, CancellationToken.None));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task SessionFactory_FirstFailSecondSuccess_CanRecoverAtCaller()
    {
        var audioPayload = BuildAudioBinary("OKAUDIO");
        var calls = 0;
        var client = new EdgeTtsClient((_, _, _) =>
        {
            calls++;
            if (calls == 1)
                throw new WebSocketException("transient");

            IEdgeTtsSession session = new FakeSession(new[]
            {
                (true, audioPayload),
                (false, Encoding.UTF8.GetBytes("Path:turn.end\r\n"))
            });
            return Task.FromResult(session);
        });

        await Assert.ThrowsAsync<WebSocketException>(() =>
            client.SynthesizeAsync("hello", TtsTextSelector.VoiceJenny, 1.0, CancellationToken.None));

        var bytes = await client.SynthesizeAsync("hello", TtsTextSelector.VoiceJenny, 1.0, CancellationToken.None);
        Assert.Equal(Encoding.ASCII.GetBytes("OKAUDIO"), bytes);
        Assert.Equal(2, calls);
    }

    // ==================== UTF-8 byte chunking (P2-3) ====================

    [Fact]
    public void SplitUtf8ByByteLength_PreservesMultiByteChineseCharacters()
    {
        // "你好" = 6 UTF-8 bytes. Each char is 3 bytes.
        var parts = EdgeTtsClient.SplitUtf8ByByteLength("你好", maxBytes: 3);
        Assert.Equal(2, parts.Count);
        Assert.Equal("你", parts[0]);
        Assert.Equal("好", parts[1]);
        // No char is split — each decoded chunk is a complete character.
        Assert.All(parts, p => Assert.True(p.Length == 1));
    }

    [Fact]
    public void SplitUtf8ByByteLength_ReassemblesToOriginal()
    {
        var original = "中国🎉世界test &amp; hello";
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(original, maxBytes: 8);
        var reassembled = string.Concat(parts);
        Assert.Equal(original, reassembled);
    }

    [Fact]
    public void SplitUtf8ByByteLength_NoReplacementCharacterInAnyChunk()
    {
        // Mix that exercises 2-byte, 3-byte, and 4-byte UTF-8 sequences.
        var mixed = "a¢你🎉测试Ωend"; // '¢' = 2B, '你' = 3B, '🎉' = 4B
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(mixed, maxBytes: 5);
        Assert.All(parts, p => Assert.DoesNotContain('\uFFFD', p));
        var reassembled = string.Concat(parts);
        Assert.Equal(mixed, reassembled);
    }

    [Fact]
    public void SplitUtf8ByByteLength_EmojiFourByteSequenceNotTruncated()
    {
        // 🎉 = 0xF0 0x9F 0x8E 0x89 (4 bytes)
        var text = "🎉🎉🎉";
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(text, maxBytes: 5);
        Assert.Equal(3, parts.Count);
        // Each chunk must decode to exactly one 🎉 without replacement characters.
        Assert.All(parts, p => Assert.Equal("🎉", p));
    }

    [Fact]
    public void SplitUtf8ByByteLength_ExactlyAtCharacterBoundary()
    {
        // "abc你好" = 3 + 6 = 9 bytes. maxBytes=3 should split as "abc", "你", "好".
        var parts = EdgeTtsClient.SplitUtf8ByByteLength("abc你好", maxBytes: 3);
        Assert.Equal(3, parts.Count);
        Assert.Equal("abc", parts[0]);
        Assert.Equal("你", parts[1]);
        Assert.Equal("好", parts[2]);
    }

    [Fact]
    public void SplitUtf8ByByteLength_SingleCharExceedsMaxBytes_IncludesAtLeastOneChar()
    {
        // 🎉 is 4 bytes. With maxBytes=2, we must still produce at least one chunk
        // containing the complete 4-byte emoji.
        var parts = EdgeTtsClient.SplitUtf8ByByteLength("🎉", maxBytes: 2);
        Assert.Single(parts);
        Assert.Equal("🎉", parts[0]);
    }

    [Fact]
    public void SplitUtf8ByByteLength_EntityWithinChunkPreserved()
    {
        var escaped = TtsTextSelector.EscapeSsml("a & b < c");
        // "a &amp; b &lt; c" — all ASCII, entities are intact.
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(escaped, maxBytes: 8);
        // Ensure no chunk has an unclosed '&' that would break SSML.
        Assert.All(parts, part => Assert.False(part.Contains('&') && !part.Contains(';')));
        var reassembled = string.Concat(parts);
        Assert.Equal(escaped, reassembled);
    }

    [Fact]
    public void SplitUtf8ByByteLength_EntityAndMultibyteMixed()
    {
        // "你好 &amp; 世界" — multibyte + entity + multibyte.
        var text = "你好 &amp; 世界";
        var parts = EdgeTtsClient.SplitUtf8ByByteLength(text, maxBytes: 7);
        Assert.All(parts, p => Assert.DoesNotContain('\uFFFD', p));
        var reassembled = string.Concat(parts);
        Assert.Equal(text, reassembled);
    }

    [Fact]
    public void SplitUtf8ByByteLength_AllAsciiTextNoSplit()
    {
        var parts = EdgeTtsClient.SplitUtf8ByByteLength("hello world", maxBytes: 3000);
        Assert.Single(parts);
        Assert.Equal("hello world", parts[0]);
    }

    [Fact]
    public void SplitUtf8ByByteLength_EmptyOrInvalidArgs()
    {
        Assert.Empty(EdgeTtsClient.SplitUtf8ByByteLength("", 100));
        Assert.Empty(EdgeTtsClient.SplitUtf8ByByteLength(null!, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EdgeTtsClient.SplitUtf8ByByteLength("text", 0));
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
