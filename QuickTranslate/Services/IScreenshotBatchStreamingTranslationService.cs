namespace QuickTranslate.Services;

/// <summary>
/// 截图结构化批量翻译的单元完成级流式能力。
/// 回调只会收到已经完整解析、校验并且属于本次请求的单元。
/// </summary>
public interface IScreenshotBatchStreamingTranslationService
{
    Task<IReadOnlyList<TranslatedTextUnit>> TranslateScreenshotBatchStreamingAsync(
        IReadOnlyList<ScreenshotTranslationUnit> units,
        string targetLanguage,
        Action<TranslatedTextUnit> onUnitCompleted,
        CancellationToken cancellationToken = default);
}
