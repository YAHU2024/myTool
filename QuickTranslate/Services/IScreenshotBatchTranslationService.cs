namespace QuickTranslate.Services;

/// <summary>
/// 为截图翻译提供一次结构化批量请求的可选能力。
/// 普通翻译服务不必实现此接口，调用方可安全回退到逐单元请求。
/// </summary>
public interface IScreenshotBatchTranslationService
{
    Task<IReadOnlyList<TranslatedTextUnit>> TranslateScreenshotBatchAsync(
        IReadOnlyList<ScreenshotTranslationUnit> units,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider 返回内容无法安全映射到截图 UnitId 时抛出。</summary>
public sealed class ScreenshotTranslationBatchFormatException : FormatException
{
    public ScreenshotTranslationBatchFormatException(string reason, Exception? inner = null)
        : base($"截图批量翻译响应无法安全映射（{reason}）。", inner)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "invalid_response" : reason;
    }

    public string Reason { get; }
}
