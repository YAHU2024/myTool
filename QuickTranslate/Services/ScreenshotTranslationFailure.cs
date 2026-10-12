using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

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
    Connect,
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
        if (exception is ScreenshotTranslationTimeoutException)
            return ScreenshotTranslationFailureKind.ProviderTimeout;
        if (cancellationRequested)
            return ScreenshotTranslationFailureKind.Cancelled;
        if (exception is OperationCanceledException)
        {
            // An OperationCanceledException without the request token being
            // cancelled is a provider/transport abort, not a user action.
            return stage.ToLowerInvariant() switch
            {
                "capture" => ScreenshotTranslationFailureKind.CaptureFailed,
                "ocr" => ScreenshotTranslationFailureKind.OcrFailed,
                _ => ScreenshotTranslationFailureKind.ProviderTransport
            };
        }
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
                408 => ScreenshotTranslationFailureKind.ProviderTimeout,
                >= 500 and <= 599 => ScreenshotTranslationFailureKind.ProviderServer,
                _ => ScreenshotTranslationFailureKind.ProviderTransport
            };
        }
        if (exception is IOException or SocketException or AuthenticationException)
        {
            return stage.ToLowerInvariant() switch
            {
                "capture" => ScreenshotTranslationFailureKind.CaptureFailed,
                "ocr" => ScreenshotTranslationFailureKind.OcrFailed,
                _ => ScreenshotTranslationFailureKind.ProviderTransport
            };
        }
        if (exception is ArgumentException argument && IsResourceLimitMessage(argument.Message))
            return ScreenshotTranslationFailureKind.ResourceLimit;
        if (exception is InvalidOperationException invalidOperation &&
            string.Equals(stage, "translation", StringComparison.OrdinalIgnoreCase) &&
            IsRequestLimitMessage(invalidOperation.Message))
            return ScreenshotTranslationFailureKind.ResourceLimit;
        if (exception is ArgumentException &&
            string.Equals(stage, "ocr", StringComparison.OrdinalIgnoreCase))
            return ScreenshotTranslationFailureKind.OcrFailed;
        if (string.Equals(stage, "capture", StringComparison.OrdinalIgnoreCase))
            return ScreenshotTranslationFailureKind.CaptureFailed;
        return ScreenshotTranslationFailureKind.Unknown;
    }

    private static bool IsResourceLimitMessage(string message)
    {
        return message.Contains("超过", StringComparison.Ordinal) ||
               message.Contains("像素", StringComparison.Ordinal) ||
               message.Contains("载荷", StringComparison.Ordinal) ||
               message.Contains("边长", StringComparison.Ordinal) ||
               message.Contains("块数", StringComparison.Ordinal) ||
               message.Contains("单元数", StringComparison.Ordinal) ||
               message.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("pixel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequestLimitMessage(string message) =>
        message.Contains("内容过长", StringComparison.Ordinal) ||
        message.Contains("最多支持", StringComparison.Ordinal);
}
