using System.Net.WebSockets;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TtsSpeakExceptionTests
{
    [Theory]
    [InlineData(typeof(TimeoutException), TtsSpeakException.Timeout)]
    [InlineData(typeof(WebSocketException), TtsSpeakException.WebSocket)]
    public void Classify_MapsKnownExceptions(Type exceptionType, string expectedKind)
    {
        Exception ex = exceptionType.Name switch
        {
            nameof(TimeoutException) => new TimeoutException("t"),
            nameof(WebSocketException) => new WebSocketException("ws"),
            _ => throw new InvalidOperationException(exceptionType.Name)
        };
        Assert.Equal(expectedKind, TtsSpeakException.Classify(ex, CancellationToken.None));
    }

    [Fact]
    public void Classify_EmptyAudioAndCancel()
    {
        Assert.Equal(
            TtsSpeakException.EmptyAudio,
            TtsSpeakException.Classify(new InvalidOperationException("Edge TTS returned empty audio."), CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(
            TtsSpeakException.Cancelled,
            TtsSpeakException.Classify(new OperationCanceledException(cts.Token), cts.Token));

        Assert.Equal(
            TtsSpeakException.Timeout,
            TtsSpeakException.Classify(new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void IsRetryable_OnlyTransientKinds()
    {
        Assert.True(TtsSpeakException.IsRetryable(TtsSpeakException.EmptyAudio));
        Assert.True(TtsSpeakException.IsRetryable(TtsSpeakException.WebSocket));
        Assert.True(TtsSpeakException.IsRetryable(TtsSpeakException.Timeout));
        Assert.False(TtsSpeakException.IsRetryable(TtsSpeakException.Cancelled));
        Assert.False(TtsSpeakException.IsRetryable(TtsSpeakException.Protocol));
        Assert.False(TtsSpeakException.IsRetryable(TtsSpeakException.Playback));
    }

    [Fact]
    public void ShouldFallbackToXiaoxiao_OnlyAutoZhEmptyAudioNonXiaoxiao()
    {
        Assert.True(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionAuto,
            "zh",
            TtsTextSelector.VoiceJenny,
            TtsSpeakException.EmptyAudio));

        Assert.True(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionAuto,
            "zh",
            TtsTextSelector.VoiceYunxi,
            TtsSpeakException.EmptyAudio));

        Assert.False(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionManual,
            "zh",
            TtsTextSelector.VoiceJenny,
            TtsSpeakException.EmptyAudio));

        Assert.False(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionAuto,
            "zh",
            TtsTextSelector.VoiceXiaoxiao,
            TtsSpeakException.EmptyAudio));

        Assert.False(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionAuto,
            "zh",
            TtsTextSelector.VoiceJenny,
            TtsSpeakException.WebSocket));

        Assert.False(TtsSpeakException.ShouldFallbackToXiaoxiao(
            TtsTextSelector.SelectionAuto,
            "en",
            TtsTextSelector.VoiceJenny,
            TtsSpeakException.EmptyAudio));
    }

    [Fact]
    public void UserFacingMessage_ManualAppendsSwitchHint()
    {
        var manual = TtsSpeakException.UserFacingMessage(
            TtsSpeakException.EmptyAudio,
            TtsTextSelector.SelectionManual);
        Assert.Contains("朗读失败", manual);
        Assert.Contains("自动", manual);

        var auto = TtsSpeakException.UserFacingMessage(
            TtsSpeakException.WebSocket,
            TtsTextSelector.SelectionAuto);
        Assert.Contains("网络", auto);
        Assert.DoesNotContain("可在设置中", auto);
    }
}
