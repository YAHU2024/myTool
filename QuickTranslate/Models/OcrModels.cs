using System.Collections.ObjectModel;

namespace QuickTranslate.Models;

/// <summary>
/// OCR 输入图像。像素为 BGRA32、预乘 alpha，按 row-major 顺序存储。
/// </summary>
public sealed record OcrImage(
    int PixelWidth,
    int PixelHeight,
    int Stride,
    ReadOnlyMemory<byte> BgraPixels)
{
    public void Validate()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
            throw new ArgumentException("OCR 图像尺寸必须大于 0。", nameof(PixelWidth));

        var minimumStride = (long)PixelWidth * 4;
        if (minimumStride > int.MaxValue || Stride < minimumStride)
            throw new ArgumentException("OCR 图像 stride 小于一行所需字节数。", nameof(Stride));

        var requiredBytes = (long)Stride * PixelHeight;
        if (requiredBytes > int.MaxValue || BgraPixels.Length < requiredBytes)
            throw new ArgumentException("OCR 图像像素载荷长度不足。", nameof(BgraPixels));
    }

    public void Validate(OcrResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Validate();
        limits.Validate(this);
    }
}

/// <summary>OCR 输入图像内的轴对齐像素矩形。</summary>
public readonly record struct OcrBounds(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool IsValid => Width > 0 && Height > 0;

    public bool IsWithin(int pixelWidth, int pixelHeight) =>
        IsValid &&
        X >= 0 &&
        Y >= 0 &&
        (long)X + Width <= pixelWidth &&
        (long)Y + Height <= pixelHeight;

    public static OcrBounds Union(OcrBounds first, OcrBounds second)
    {
        if (!first.IsValid)
            return second;
        if (!second.IsValid)
            return first;

        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Max((long)first.Y + first.Height, (long)second.Y + second.Height);
        return new(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }
}

/// <summary>OCR 识别出的一个文本块。坐标相对于输入图像左上角。</summary>
public sealed record OcrTextBlock(
    string BlockId,
    string Text,
    OcrBounds Bounds,
    double? Confidence = null);

/// <summary>一次识别的完整结果。</summary>
public sealed record OcrResult(
    IReadOnlyList<OcrTextBlock> Blocks,
    string UsedLanguageTag,
    bool LanguageFallbackUsed,
    double TextAngleDegrees,
    TimeSpan Elapsed);

/// <summary>OCR 引擎能力探测结果。</summary>
public sealed record OcrCapability(
    bool IsAvailable,
    string UnavailableReason,
    IReadOnlyList<string> SupportedLanguageTags,
    int? MaxImageDimension)
{
    public static OcrCapability Unavailable(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason) ? "OCR 引擎不可用。" : reason, Array.Empty<string>(), null);

    public static OcrCapability Available(
        IEnumerable<string> supportedLanguageTags,
        int? maxImageDimension) =>
        CreateAvailable(supportedLanguageTags, maxImageDimension);

    private static OcrCapability CreateAvailable(
        IEnumerable<string> supportedLanguageTags,
        int? maxImageDimension)
    {
        ArgumentNullException.ThrowIfNull(supportedLanguageTags);
        if (maxImageDimension is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxImageDimension));

        return new(
            true,
            string.Empty,
            new ReadOnlyCollection<string>(supportedLanguageTags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()),
            maxImageDimension);
    }
}

/// <summary>一次识别的语言选择和容错策略。</summary>
public sealed record OcrRecognitionOptions(
    string? LanguageHint = null,
    bool AllowLanguageFallback = true);

/// <summary>
/// M0 冻结的截图/OCR 资源限制。超限时必须拒绝，不得静默截断输入。
/// </summary>
public sealed record OcrResourceLimits(
    int MaxImageDimension = 10_000,
    long MaxPixelCount = 8_388_608,
    long MaxPayloadBytes = 33_554_432,
    int MaxBlockCount = 256,
    int MaxNormalizedTextLength = 16_384,
    int MaxTranslationUnitCount = 128)
{
    public static OcrResourceLimits Default { get; } = new();

    public void Validate(OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (MaxImageDimension <= 0 || MaxPixelCount <= 0 || MaxPayloadBytes <= 0 ||
            MaxBlockCount <= 0 || MaxNormalizedTextLength <= 0 || MaxTranslationUnitCount <= 0)
        {
            throw new ArgumentException("OCR 资源限制必须为正数。", nameof(MaxImageDimension));
        }

        if (image.PixelWidth > MaxImageDimension || image.PixelHeight > MaxImageDimension)
            throw new ArgumentException("OCR 图像尺寸超过允许的最大边长。", nameof(image));
        if ((long)image.PixelWidth * image.PixelHeight > MaxPixelCount)
            throw new ArgumentException("OCR 图像像素总数超过允许上限。", nameof(image));
        if ((long)image.Stride * image.PixelHeight > MaxPayloadBytes)
            throw new ArgumentException("OCR 图像像素载荷超过允许上限。", nameof(image));
    }
}
