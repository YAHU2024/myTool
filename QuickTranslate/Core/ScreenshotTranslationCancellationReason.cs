namespace QuickTranslate.Core;

/// <summary>Identifies the owner that cancelled an active screenshot translation session.</summary>
public enum ScreenshotTranslationCancellationReason
{
    User,
    OverlayClosed,
    OcrEngineSwitch,
    ApplicationExit,
    PipelineTimeout,
    Unknown
}
