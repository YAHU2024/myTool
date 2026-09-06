using QuickTranslate.Models;

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
    public static ScreenshotSceneEvidence FromOcrResult(OcrResult result, bool workerAvailable)
    {
        ArgumentNullException.ThrowIfNull(result);
        var blocks = result.Blocks.Where(static b => !string.IsNullOrWhiteSpace(b.Text)).ToArray();
        var bubbles = blocks.Count(static b => b.RegionType == OcrRegionType.Bubble);
        var ratio = blocks.Length == 0 ? 0 : (double)bubbles / blocks.Length;
        var score = Math.Clamp(ratio * 0.75 + (bubbles >= 2 ? 0.25 : 0), 0, 1);
        return new(blocks.Length, bubbles, score, workerAvailable);
    }

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
