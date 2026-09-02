using System.Net;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationFailureTests
{
    [Fact]
    public void Classify_HttpTransportFailure_IsNotResourceLimit()
    {
        var kind = ScreenshotTranslationFailureClassifier.Classify(
            new HttpRequestException("transport", null),
            "translation",
            cancellationRequested: false);

        Assert.Equal(ScreenshotTranslationFailureKind.ProviderTransport, kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ScreenshotTranslationFailureKind.ProviderUnauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ScreenshotTranslationFailureKind.ProviderQuota)]
    [InlineData(HttpStatusCode.BadGateway, ScreenshotTranslationFailureKind.ProviderServer)]
    public void Classify_HttpStatus_UsesActionableProviderKind(
        HttpStatusCode statusCode,
        ScreenshotTranslationFailureKind expected)
    {
        var kind = ScreenshotTranslationFailureClassifier.Classify(
            new HttpRequestException("provider", null, statusCode),
            "translation",
            cancellationRequested: false);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_TimeoutAndCancellationRemainDistinct()
    {
        Assert.Equal(
            ScreenshotTranslationFailureKind.ProviderTimeout,
            ScreenshotTranslationFailureClassifier.Classify(
                new ScreenshotTranslationTimeoutException(ScreenshotTranslationTimeoutKind.Idle),
                "translation",
                cancellationRequested: false));
        Assert.Equal(
            ScreenshotTranslationFailureKind.Cancelled,
            ScreenshotTranslationFailureClassifier.Classify(
                new OperationCanceledException(),
                "translation",
                cancellationRequested: true));
    }

    [Fact]
    public void Classify_OcrArgumentFailure_IsResourceLimit()
    {
        var kind = ScreenshotTranslationFailureClassifier.Classify(
            new ArgumentException("OCR 图像像素总数超过允许上限。"),
            "ocr",
            cancellationRequested: false);

        Assert.Equal(ScreenshotTranslationFailureKind.ResourceLimit, kind);
    }

    [Fact]
    public void Classify_CaptureResourceFailure_IsResourceLimit()
    {
        var kind = ScreenshotTranslationFailureClassifier.Classify(
            new ArgumentException("截图区域超过允许的像素总数。"),
            "capture",
            cancellationRequested: false);

        Assert.Equal(ScreenshotTranslationFailureKind.ResourceLimit, kind);
    }
}
