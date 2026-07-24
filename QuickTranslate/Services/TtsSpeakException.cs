using System.IO;
namespace QuickTranslate.Services;

/// <summary>
/// Structured TTS failure for UI status messages and diagnostics (never carries spoken text).
/// </summary>
public sealed class TtsSpeakException : Exception
{
    public const string EmptyAudio = "empty_audio";
    public const string WebSocket = "websocket";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string Protocol = "protocol";
    public const string Playback = "playback";

    public TtsSpeakException(
        string errorKind,
        string selectionMode,
        string voice,
        int attempt,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
        SelectionMode = selectionMode;
        Voice = voice;
        Attempt = attempt;
    }

    public string ErrorKind { get; }
    public string SelectionMode { get; }
    public string Voice { get; }
    public int Attempt { get; }

    public static bool IsRetryable(string errorKind) =>
        errorKind is EmptyAudio or WebSocket or Timeout;

    public static bool ShouldFallbackToXiaoxiao(
        string selectionMode,
        string languageHint,
        string voice,
        string errorKind) =>
        string.Equals(selectionMode, TtsTextSelector.SelectionAuto, StringComparison.Ordinal)
        && string.Equals(errorKind, EmptyAudio, StringComparison.Ordinal)
        && string.Equals(languageHint, "zh", StringComparison.Ordinal)
        && !string.Equals(voice, TtsTextSelector.VoiceXiaoxiao, StringComparison.OrdinalIgnoreCase);

    public static string Classify(Exception ex, CancellationToken userToken)
    {
        if (ex is TtsSpeakException speak)
            return speak.ErrorKind;

        if (ex is OperationCanceledException)
            return userToken.IsCancellationRequested ? Cancelled : Timeout;

        if (ex is TimeoutException)
            return Timeout;

        if (ex is System.Net.WebSockets.WebSocketException)
            return WebSocket;

        if (ex is InvalidOperationException invalid)
        {
            var msg = invalid.Message ?? string.Empty;
            if (msg.Contains("empty", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Empty", StringComparison.Ordinal))
            {
                return EmptyAudio;
            }

            if (msg.Contains("playback", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Media", StringComparison.OrdinalIgnoreCase))
            {
                return Playback;
            }
        }

        if (ex is IOException or System.Net.Http.HttpRequestException)
            return WebSocket;

        return Protocol;
    }

    public static string UserFacingMessage(string errorKind, string selectionMode)
    {
        var core = errorKind switch
        {
            EmptyAudio => "朗读失败：未获得音频",
            WebSocket => "朗读失败：网络连接异常",
            Timeout => "朗读失败：超时",
            Playback => "朗读失败：播放异常",
            Protocol => "朗读失败：协议错误",
            Cancelled => "朗读已取消",
            _ => "朗读失败"
        };

        if (string.Equals(selectionMode, TtsTextSelector.SelectionManual, StringComparison.Ordinal)
            && errorKind is not Cancelled)
        {
            return core + "。可在设置中改用自动或与文本语言匹配的音色";
        }

        return core;
    }
}

