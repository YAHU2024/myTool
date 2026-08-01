using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslate.Services;

/// <summary>
/// Minimal Edge read-aloud WebSocket client. Protocol details stay inside this type.
/// </summary>
public sealed class EdgeTtsClient
{
    internal const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    internal const string ChromiumFullVersion = "143.0.3650.75";
    internal const string SecMsGecVersion = "1-" + ChromiumFullVersion;
    private const long WinEpochSeconds = 11644473600;
    private const int MaxSsmlTextBytes = 3000;
    private const int DefaultTimeoutMs = 15_000;

    private readonly Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<IEdgeTtsSession>> _sessionFactory;

    public EdgeTtsClient()
        : this(CreateDefaultSessionAsync)
    {
    }

    internal EdgeTtsClient(
        Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<IEdgeTtsSession>> sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        string voice,
        double rate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required.", nameof(voice));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeoutMs);
        var ct = timeoutCts.Token;

        try
        {
            var cleaned = TtsTextSelector.NormalizeForSpeech(text, maxChars: 0, out _);
            if (string.IsNullOrWhiteSpace(cleaned))
                throw new InvalidOperationException("No speakable text after normalization.");

            var escaped = TtsTextSelector.EscapeSsml(cleaned);
            var chunks = SplitUtf8ByByteLength(escaped, MaxSsmlTextBytes);
            using var audio = new MemoryStream();

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var part = await SynthesizeChunkAsync(voice, chunk, rate, ct).ConfigureAwait(false);
                if (part.Length > 0)
                    await audio.WriteAsync(part, ct).ConfigureAwait(false);
            }

            if (audio.Length == 0)
                throw new InvalidOperationException("Edge TTS returned empty audio.");

            return audio.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Edge TTS synthesize timed out.");
        }
    }

    internal static string GenerateSecMsGec(DateTimeOffset utcNow, double clockSkewSeconds = 0)
    {
        var ticks = utcNow.ToUnixTimeMilliseconds() / 1000.0 + clockSkewSeconds;
        ticks += WinEpochSeconds;
        ticks -= ticks % 300;
        ticks *= 1e7;
        var payload = ((long)ticks).ToString(CultureInfo.InvariantCulture) + TrustedClientToken;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    internal static Uri BuildWebSocketUri(string connectionId, string secMsGec)
    {
        var query =
            "TrustedClientToken=" + TrustedClientToken +
            "&ConnectionId=" + connectionId +
            "&Sec-MS-GEC=" + secMsGec +
            "&Sec-MS-GEC-Version=" + SecMsGecVersion;
        return new Uri("wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?" + query);
    }

    internal static IReadOnlyDictionary<string, string> BuildWebSocketHeaders()
    {
        var major = ChromiumFullVersion.Split('.', 2)[0];
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
            ["Origin"] = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold",
            ["User-Agent"] =
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0",
            ["Accept-Encoding"] = "gzip, deflate, br, zstd",
            ["Accept-Language"] = "en-US,en;q=0.9"
        };
    }

    internal static string BuildSpeechConfigMessage(DateTimeOffset utcNow)
    {
        var timestamp = FormatJsDate(utcNow);
        return
            $"X-Timestamp:{timestamp}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
            "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"" +
            "},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
    }

    internal static string BuildSsmlMessage(string requestId, DateTimeOffset utcNow, string ssml)
    {
        var timestamp = FormatJsDate(utcNow);
        return
            $"X-RequestId:{requestId}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{timestamp}Z\r\n" +
            "Path:ssml\r\n\r\n" +
            ssml;
    }

    internal static bool TryExtractAudioFromBinaryMessage(ReadOnlySpan<byte> message, out byte[] audio)
    {
        audio = Array.Empty<byte>();
        if (message.Length < 2)
            return false;

        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(message);
        var headerEnd = 2 + headerLength;
        if (headerEnd > message.Length)
            return false;

        var headers = Encoding.UTF8.GetString(message.Slice(2, headerLength));
        if (headers.IndexOf("Path:audio", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var dataStart = headerEnd;
        if (dataStart + 2 <= message.Length &&
            message[dataStart] == (byte)'\r' &&
            message[dataStart + 1] == (byte)'\n')
        {
            dataStart += 2;
        }

        if (dataStart >= message.Length)
            return false;

        audio = message.Slice(dataStart).ToArray();
        return audio.Length > 0;
    }

    internal static bool IsTurnEndMessage(string textMessage) =>
        textMessage.IndexOf("Path:turn.end", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static IReadOnlyList<string> SplitUtf8ByByteLength(string text, int maxBytes)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var utf8 = Encoding.UTF8;
        var bytes = utf8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return new[] { text };

        var parts = new List<string>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var remaining = bytes.Length - offset;
            var take = Math.Min(maxBytes, remaining);

            // Align the chunk end to a complete UTF-8 character boundary.
            take = AlignUtf8ChunkEnd(bytes, offset, take);

            // When maxBytes is too small for a single multi-byte character,
            // include at least one complete character to make forward progress.
            if (take <= 0)
            {
                var firstByte = bytes[offset];
                take = Math.Min(Utf8ByteSequenceLength(firstByte), remaining);
            }

            // Avoid splitting an SSML entity such as &amp;.
            var slice = bytes.AsSpan(offset, take);
            var amp = slice.LastIndexOf((byte)'&');
            if (amp >= 0 && slice.Slice(amp).IndexOf((byte)';') < 0 && amp > 0)
            {
                take = amp;
                // Re-align after entity adjustment — & may have been preceded
                // by a multi-byte character whose boundary we must now honour.
                take = AlignUtf8ChunkEnd(bytes, offset, take);
            }

            parts.Add(utf8.GetString(bytes, offset, take));
            offset += take;
        }

        return parts;
    }

    /// <summary>
    /// Backtracks <paramref name="take"/> so that the span
    /// <c>bytes[offset .. offset+take]</c> ends on a complete UTF-8 character.
    /// </summary>
    internal static int AlignUtf8ChunkEnd(byte[] bytes, int offset, int take)
    {
        while (take > 0)
        {
            var last = bytes[offset + take - 1];
            // Continuation byte (10xxxxxx) — always part of an incomplete sequence.
            if ((last & 0xC0) == 0x80)
            {
                take--;
                continue;
            }

            // Multi-byte lead byte — its continuation bytes are beyond the chunk.
            var seqLen = Utf8ByteSequenceLength(last);
            if (seqLen > 1)
            {
                take--;
                continue;
            }

            // Single-byte character (0xxxxxxx) — valid boundary.
            break;
        }

        return take;
    }

    /// <summary>
    /// Returns the number of bytes in the UTF-8 sequence started by <paramref name="firstByte"/>.
    /// </summary>
    private static int Utf8ByteSequenceLength(byte firstByte)
    {
        if ((firstByte & 0x80) == 0) return 1;     // 0xxxxxxx
        if ((firstByte & 0xE0) == 0xC0) return 2;  // 110xxxxx
        if ((firstByte & 0xF0) == 0xE0) return 3;  // 1110xxxx
        return 4;                                    // 11110xxx (emoji, etc.)
    }

    private async Task<byte[]> SynthesizeChunkAsync(
        string voice,
        string escapedTextChunk,
        double rate,
        CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var requestId = Guid.NewGuid().ToString("N");
        var sec = GenerateSecMsGec(DateTimeOffset.UtcNow);
        var uri = BuildWebSocketUri(connectionId, sec);
        var headers = BuildWebSocketHeaders();
        var ssml = TtsTextSelector.BuildSsml(voice, escapedTextChunk, rate);

        await using var session = await _sessionFactory(uri, headers, cancellationToken).ConfigureAwait(false);
        await session.SendTextAsync(BuildSpeechConfigMessage(DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
        await session.SendTextAsync(BuildSsmlMessage(requestId, DateTimeOffset.UtcNow, ssml), cancellationToken)
            .ConfigureAwait(false);

        using var audio = new MemoryStream();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
                break;

            if (message.Value.IsBinary)
            {
                if (TryExtractAudioFromBinaryMessage(message.Value.Payload, out var part))
                    await audio.WriteAsync(part, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var text = Encoding.UTF8.GetString(message.Value.Payload);
            if (IsTurnEndMessage(text))
                break;
        }

        return audio.ToArray();
    }

    private static string FormatJsDate(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture)
        + " GMT+0000 (Coordinated Universal Time)";

    private static async Task<IEdgeTtsSession> CreateDefaultSessionAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        foreach (var header in headers)
        {
            // ClientWebSocket rejects some restricted headers; set only safe ones.
            if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Pragma", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                try { socket.Options.SetRequestHeader(header.Key, header.Value); }
                catch (ArgumentException) { /* Restricted header on this runtime. */ }
            }
        }

        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        return new ClientWebSocketEdgeTtsSession(socket);
    }
}

internal interface IEdgeTtsSession : IAsyncDisposable
{
    Task SendTextAsync(string message, CancellationToken cancellationToken);
    Task<(bool IsBinary, byte[] Payload)?> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketEdgeTtsSession : IEdgeTtsSession
{
    private readonly ClientWebSocket _socket;
    private readonly byte[] _buffer = new byte[64 * 1024];

    public ClientWebSocketEdgeTtsSession(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(bool IsBinary, byte[] Payload)?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(_buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return (result.MessageType == WebSocketMessageType.Binary, ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort close.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}

