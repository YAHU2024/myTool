using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record MangaSceneRoutingResult(
    ScreenshotSceneRoute Route,
    MangaWorkerResponse? WorkerResponse,
    string? TemporaryImagePath);

/// <summary>Coordinates a one-shot manga probe without exposing process details to WPF.</summary>
public sealed class MangaSceneRoutingService
{
    private readonly MangaWorkerClient _client;

    public MangaSceneRoutingService(MangaWorkerClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public static void CleanupTemporaryImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QuickTranslate", "manga-worker"));
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                File.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<MangaSceneRoutingResult> ProbeAsync(
        OcrImage image,
        string requestId,
        string sourceLanguage,
        bool enabled,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        if (!enabled)
            return new(ScreenshotSceneRoute.StandardOcr, null, null);

        var tempDir = Path.Combine(Path.GetTempPath(), "QuickTranslate", "manga-worker");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, requestId + ".png");
        try
        {
            SavePng(image, path);
            var request = MangaWorkerRequestFactory.Create(requestId, path, sourceLanguage, false, tempDir);
            var response = await _client.RunAsync(request, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            var evidence = MangaWorkerEvidenceParser.ToSceneEvidence(response);
            var route = response.Type == "completed"
                ? ScreenshotSceneRouter.Decide(true, evidence)
                : ScreenshotSceneRoute.FallbackStandardOcr;
            return new(route, response, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path);
        }
        catch (Exception)
        {
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path);
        }
    }

    private static void SavePng(OcrImage image, string path)
    {
        var bitmap = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, image.BgraPixels.ToArray(), image.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
