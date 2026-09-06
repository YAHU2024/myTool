namespace QuickTranslate.Services;

public enum ScreenshotSceneRoute
{
    StandardOcr,
    MangaWorker,
    FallbackStandardOcr
}

public sealed record ScreenshotSceneEvidence(
    int BlockCount,
    int BubbleBlockCount,
    double MangaScore,
    bool WorkerAvailable);

/// <summary>Conservative scene routing: enhancement requires strong evidence and an available worker.</summary>
public static class ScreenshotSceneRouter
{
    public static ScreenshotSceneRoute Decide(bool enhancementEnabled, ScreenshotSceneEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!enhancementEnabled)
            return ScreenshotSceneRoute.StandardOcr;
        if (!evidence.WorkerAvailable)
            return ScreenshotSceneRoute.FallbackStandardOcr;
        if (evidence.BlockCount >= 2 && evidence.BubbleBlockCount >= 2 && evidence.MangaScore >= 0.70)
            return ScreenshotSceneRoute.MangaWorker;
        return ScreenshotSceneRoute.StandardOcr;
    }
}
