using System.Net;
using System.Net.Http;

namespace QuickTranslate.Services;

public enum ScreenshotTranslationFailureKind
{
    CaptureFailed,
    ResourceLimit,
    OcrUnavailable,
    OcrFailed,
    ProviderUnauthorized,
    ProviderQuota,
    ProviderServer,
    ProviderTransport,
    ProviderTimeout,
    ProviderFormat,
    Cancelled,
    Unknown
}

public enum ScreenshotTranslationTimeoutKind
{
    FirstChunk,
    Idle,
    Overall
}

public sealed class ScreenshotTranslationTimeoutException : TimeoutException
{
    public ScreenshotTranslationTimeoutException(
        ScreenshotTranslationTimeoutKind timeoutKind,
        string? message = null,
        Exception? inner = null)
        : base(message ?? $"截图翻译流式请求超时（{timeoutKind}）。", inner) =>
        TimeoutKind = timeoutKind;

    public ScreenshotTranslationTimeoutKind TimeoutKind { get; }
}

public static class ScreenshotTranslationFailureClassifier
{
    public static ScreenshotTranslationFailureKind Classify(
        Exception exception,
        string stage,
        bool cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (cancellationRequested || exception is OperationCanceledException)
            return ScreenshotTranslationFailureKind.Cancelled;
        if (exception is ScreenshotTranslationTimeoutException)
            return ScreenshotTranslationFailureKind.ProviderTimeout;
        if (exception is OcrEngineUnavailableException)
            return ScreenshotTranslationFailureKind.OcrUnavailable;
        if (exception is OcrRecognitionException)
            return ScreenshotTranslationFailureKind.OcrFailed;
        if (exception is ScreenshotTranslationBatchFormatException)
            return ScreenshotTranslationFailureKind.ProviderFormat;
        if (exception is HttpRequestException http)
        {
            var statusCode = http.StatusCode is { } status ? (int)status : 0;
            return statusCode switch
            {
                401 or 403 => ScreenshotTranslationFailureKind.ProviderUnauthorized,
                429 => ScreenshotTranslationFailureKind.ProviderQuota,
                >= 500 and <= 599 => ScreenshotTranslationFailureKind.ProviderServer,
                _ => ScreenshotTranslationFailureKind.ProviderTransport
            };
        }
        if (exception is ArgumentException argument && IsResourceLimit(argument))
            return ScreenshotTranslationFailureKind.ResourceLimit;
        if (exception is ArgumentException &&
            string.Equals(stage, "ocr", StringComparison.OrdinalIgnoreCase))
            return ScreenshotTranslationFailureKind.OcrFailed;
        if (string.Equals(stage, "capture", StringComparison.OrdinalIgnoreCase))
            return ScreenshotTranslationFailureKind.CaptureFailed;
        return ScreenshotTranslationFailureKind.Unknown;
    }

    private static bool IsResourceLimit(ArgumentException exception)
    {
        var message = exception.Message;
        return message.Contains("超过", StringComparison.Ordinal) ||
               message.Contains("像素", StringComparison.Ordinal) ||
               message.Contains("载荷", StringComparison.Ordinal) ||
               message.Contains("边长", StringComparison.Ordinal) ||
               message.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("pixel", StringComparison.OrdinalIgnoreCase);
    }
}
