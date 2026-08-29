using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>与具体 OCR 引擎隔离的本地识别契约。</summary>
public interface IOcrService
{
    OcrCapability Probe();

    /// <summary>没有文字时返回空 Blocks，而不是抛出异常。</summary>
    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        OcrRecognitionOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class OcrEngineUnavailableException : Exception
{
    public OcrEngineUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

public sealed class OcrRecognitionException : Exception
{
    public OcrRecognitionException(string message, Exception? inner = null)
        : base(message, inner) { }
}
