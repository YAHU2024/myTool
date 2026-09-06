using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record MangaSceneRoutingResult(
    ScreenshotSceneRoute Route,
    MangaWorkerResponse? WorkerResponse,
    string? TemporaryImagePath,
    string? CleanedImagePath = null,
    string? SourceLanguage = null,
    string? FailureType = null);

/// <summary>Coordinates a one-shot manga probe without exposing process details to WPF.</summary>
public sealed class MangaSceneRoutingService
{
    public const long DefaultMaxPixels = 20_000_000;
    private readonly MangaWorkerClient _client;
    private readonly string? _japaneseModelDirectory;
    private readonly string? _inpaintingModelDirectory;

    public MangaSceneRoutingService(
        MangaWorkerClient client,
        string? japaneseModelDirectory = null,
        string? inpaintingModelDirectory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _japaneseModelDirectory = string.IsNullOrWhiteSpace(japaneseModelDirectory) ? null : Path.GetFullPath(japaneseModelDirectory);
        _inpaintingModelDirectory = string.IsNullOrWhiteSpace(inpaintingModelDirectory) ? null : Path.GetFullPath(inpaintingModelDirectory);
    }

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

    public static OcrImage LoadPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("图片路径不能为空。", nameof(path));
        var decoder = new PngBitmapDecoder(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return new OcrImage(source.PixelWidth, source.PixelHeight, stride, pixels);
    }

    public async Task<MangaSceneRoutingResult> ProbeAsync(
        OcrImage image,
        string requestId,
        string sourceLanguage,
        bool enabled,
        TimeSpan timeout,
        long maxPixels = DefaultMaxPixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        if (!enabled)
            return new(ScreenshotSceneRoute.StandardOcr, null, null);
        if ((long)image.PixelWidth * image.PixelHeight > maxPixels)
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, null);

        var tempDir = Path.Combine(Path.GetTempPath(), "QuickTranslate", "manga-worker");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, requestId + ".png");
        try
        {
            SavePng(image, path);
            var request = MangaWorkerRequestFactory.CreateProbe(
                requestId,
                path,
                sourceLanguage,
                tempDir,
                _japaneseModelDirectory,
                _inpaintingModelDirectory);
            var response = await _client.RunAsync(request, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            var evidence = MangaWorkerEvidenceParser.ToSceneEvidence(response);
            var route = response.Type == "completed"
                ? ScreenshotSceneRouter.Decide(true, evidence)
                : ScreenshotSceneRoute.FallbackStandardOcr;
            return new(route, response, path, null, sourceLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupTemporaryImage(path);
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path, null, sourceLanguage, nameof(OperationCanceledException));
        }
        catch (Exception ex)
        {
            CleanupTemporaryImage(path);
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path, null, sourceLanguage, ex.GetType().Name);
        }
    }

    public async Task<MangaSceneRoutingResult> ProcessAsync(
        OcrImage image,
        string requestId,
        string sourceLanguage,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        var tempDir = Path.Combine(Path.GetTempPath(), "QuickTranslate", "manga-worker");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, requestId + ".png");
        try
        {
            SavePng(image, path);
            var request = MangaWorkerRequestFactory.Create(
                requestId,
                path,
                sourceLanguage,
                true,
                tempDir,
                true,
                _japaneseModelDirectory,
                _inpaintingModelDirectory);
            var response = await _client.RunAsync(request, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            var evidence = MangaWorkerEvidenceParser.ToSceneEvidence(response);
            var route = response.Type == "completed" ? ScreenshotSceneRouter.Decide(true, evidence) : ScreenshotSceneRoute.FallbackStandardOcr;
            var cleaned = response.Payload.TryGetProperty("cleaned_image_path", out var cleanedElement) && cleanedElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? cleanedElement.GetString() : null;
            return new(route, response, path, cleaned, sourceLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupTemporaryImage(path);
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path, null, sourceLanguage, nameof(OperationCanceledException));
        }
        catch (Exception ex)
        {
            CleanupTemporaryImage(path);
            return new(ScreenshotSceneRoute.FallbackStandardOcr, null, path, null, sourceLanguage, ex.GetType().Name);
        }
    }

    public async Task<MangaSceneRoutingResult> ProbeFromOcrAsync(
        OcrImage image,
        OcrResult standardOcr,
        string requestId,
        bool enabled,
        TimeSpan timeout,
        long maxPixels = DefaultMaxPixels,
        CancellationToken cancellationToken = default)
    {
        var language = ScreenshotLanguageRouter.Detect(standardOcr).ToWorkerLanguage();
        if (language is null)
            return new(ScreenshotSceneRoute.StandardOcr, null, null);
        return await ProbeAsync(image, requestId, language, enabled, timeout, maxPixels, cancellationToken).ConfigureAwait(false);
    }

    private static void SavePng(OcrImage image, string path)
    {
        var bitmap = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, image.BgraPixels.ToArray(), image.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
