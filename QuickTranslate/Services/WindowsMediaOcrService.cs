using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using QuickTranslate.Core;
using QuickTranslate.Models;
using ModelOcrResult = QuickTranslate.Models.OcrResult;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace QuickTranslate.Services;

/// <summary>
/// Windows 10+ built-in OCR adapter. WinRT types stay inside this service;
/// callers receive only the engine-independent OCR contract.
/// </summary>
public sealed class WindowsMediaOcrService : IOcrService
{
    private readonly OcrResourceLimits _limits;
    private readonly OcrCapability _capability;

    public WindowsMediaOcrService(OcrResourceLimits? limits = null)
    {
        _limits = limits ?? OcrResourceLimits.Default;
        _capability = ProbeCapability();
    }

    public OcrCapability Probe() => _capability;

    public async Task<ModelOcrResult> RecognizeAsync(
        OcrImage image,
        OcrRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate(_limits);
        options ??= new OcrRecognitionOptions();

        if (!_capability.IsAvailable)
            throw new OcrEngineUnavailableException(_capability.UnavailableReason);

        var selection = OcrLanguageSelector.Select(
            _capability.SupportedLanguageTags,
            options.LanguageHint,
            options.AllowLanguageFallback,
            CultureInfo.CurrentUICulture.Name);
        if (!selection.IsAvailable || selection.SelectedLanguageTag is null)
        {
            throw new OcrEngineUnavailableException(
                $"没有可用的 OCR 语言（请求：{options.LanguageHint ?? "用户语言"}）。");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(
                new Language(selection.SelectedLanguageTag));
            if (engine is null)
            {
                throw new OcrEngineUnavailableException(
                    $"OCR 语言不可用：{selection.SelectedLanguageTag}。");
            }

            using var bitmap = CreateSoftwareBitmap(image);
            var recognized = await engine
                .RecognizeAsync(bitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var blocks = BuildBlocks(recognized, image.PixelWidth, image.PixelHeight);
            watch.Stop();
            OcrBlockValidator.ValidateAll(blocks, image.PixelWidth, image.PixelHeight);

            var angle = recognized.TextAngle is { } textAngle &&
                        !double.IsNaN(textAngle) && !double.IsInfinity(textAngle)
                ? textAngle
                : 0d;
            return new(
                blocks,
                selection.SelectedLanguageTag,
                selection.FallbackUsed,
                angle,
                watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OcrEngineUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OcrRecognitionException("Windows OCR 识别失败。", ex);
        }
    }

    private static OcrCapability ProbeCapability()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages
                .Select(static language => language.LanguageTag)
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var maxImageDimension = OcrEngine.MaxImageDimension <= int.MaxValue
                ? (int)OcrEngine.MaxImageDimension
                : (int?)null;
            return OcrCapability.Available(
                languages,
                maxImageDimension,
                engineId: "windows-media-ocr",
                supportsPolygons: false,
                supportsConfidence: false);
        }
        catch (Exception ex)
        {
            return OcrCapability.Unavailable(
                $"Windows OCR 不可用（{ex.GetType().Name}）。");
        }
    }

    private static SoftwareBitmap CreateSoftwareBitmap(OcrImage image)
    {
        // CreateCopyFromBuffer assumes a tightly packed row. Copying row by row
        // prevents stride padding from being interpreted as image pixels.
        var rowBytes = checked(image.PixelWidth * 4);
        var packed = new byte[checked(rowBytes * image.PixelHeight)];
        var source = image.BgraPixels.Span;
        for (var row = 0; row < image.PixelHeight; row++)
            source.Slice(row * image.Stride, rowBytes)
                .CopyTo(packed.AsSpan(row * rowBytes, rowBytes));

        return SoftwareBitmap.CreateCopyFromBuffer(
            packed.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            image.PixelWidth,
            image.PixelHeight,
            BitmapAlphaMode.Premultiplied);
    }

    private static IReadOnlyList<OcrTextBlock> BuildBlocks(
        WinOcrResult result,
        int pixelWidth,
        int pixelHeight)
    {
        var blocks = new List<OcrTextBlock>();
        var blockNumber = 1;
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var words = line.Words
                .Where(static word => word.BoundingRect.Width > 0 && word.BoundingRect.Height > 0)
                .ToArray();
            if (words.Length == 0)
                continue;

            var left = words.Min(static word => word.BoundingRect.X);
            var top = words.Min(static word => word.BoundingRect.Y);
            var right = words.Max(static word => word.BoundingRect.X + word.BoundingRect.Width);
            var bottom = words.Max(static word => word.BoundingRect.Y + word.BoundingRect.Height);
            var bounds = ToBounds(left, top, right, bottom, pixelWidth, pixelHeight);
            if (!bounds.IsValid)
                continue;

            blocks.Add(new OcrTextBlock(
                $"b{blockNumber++:0000}",
                text,
                bounds));
        }

        return blocks;
    }

    private static OcrBounds ToBounds(
        double left,
        double top,
        double right,
        double bottom,
        int pixelWidth,
        int pixelHeight)
    {
        var x = Math.Clamp((int)Math.Floor(left), 0, pixelWidth);
        var y = Math.Clamp((int)Math.Floor(top), 0, pixelHeight);
        var maxRight = Math.Clamp((int)Math.Ceiling(right), 0, pixelWidth);
        var maxBottom = Math.Clamp((int)Math.Ceiling(bottom), 0, pixelHeight);
        return new(x, y, maxRight - x, maxBottom - y);
    }
}
