using QuickTranslate.Models;

namespace QuickTranslate.Core;

/// <summary>
/// 在截图局部物理像素坐标中为译文块计算确定性布局。
/// 该类不依赖 WPF；窗口层只负责把结果换算成当前显示器的 DIP。
/// </summary>
public sealed class OverlayLayoutEngine
{
    private readonly ScreenshotOverlayLayoutOptions _options;
    private readonly Func<string, double, int, OverlayTextMeasurement> _measure;

    public OverlayLayoutEngine(
        ScreenshotOverlayLayoutOptions? options = null,
        Func<string, double, int, OverlayTextMeasurement>? measure = null)
    {
        _options = options ?? new ScreenshotOverlayLayoutOptions();
        _options.Validate();
        _measure = measure ?? MeasureApproximate;
    }

    public ScreenshotOverlayLayoutResult Layout(
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<ScreenshotOverlayItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        var indexed = items
            .Select((item, index) => new IndexedItem(item, index))
            .OrderByDescending(static entry => ConfidencePriority(entry.Item.Confidence))
            .ThenByDescending(static entry => entry.Item.Bounds.IsValid ? (long)entry.Item.Bounds.Width * entry.Item.Bounds.Height : 0)
            .ThenBy(static entry => entry.Item.Bounds.Y)
            .ThenBy(static entry => entry.Item.Bounds.X)
            .ThenBy(static entry => entry.Item.UnitId, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Index)
            .ToArray();

        var placed = new List<PlacedRect>();
        var output = new ScreenshotOverlayLayout[items.Count];
        foreach (var entry in indexed)
            output[entry.Index] = LayoutOne(entry, pixelWidth, pixelHeight, placed);

        return new ScreenshotOverlayLayoutResult(output);
    }

    /// <summary>
    /// 使用已有卡片作为占用区域，为一个新译文计算局部布局。
    /// </summary>
    public ScreenshotOverlayLayout LayoutIncremental(
        int pixelWidth,
        int pixelHeight,
        ScreenshotOverlayItem item,
        IReadOnlyList<OcrBounds> occupiedBounds)
    {
        ArgumentNullException.ThrowIfNull(occupiedBounds);
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        var placed = occupiedBounds
            .Where(static bounds => bounds.IsValid)
            .Select(static (bounds, index) => new PlacedRect(bounds, index))
            .ToList();
        return LayoutOne(new IndexedItem(item, 0), pixelWidth, pixelHeight, placed);
    }

    /// <summary>
    /// 在既定卡片范围内为新译文选择不超过首选字号的最大可用字号。
    /// 卡片位置和尺寸保持不变，供流式覆盖层替换文本时使用。
    /// </summary>
    public bool TryFitFontSize(
        string text,
        OcrBounds bounds,
        double preferredFontSize,
        out double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        fontSize = 0;
        if (!bounds.IsValid)
            return false;

        var candidateFontSize = Math.Clamp(preferredFontSize, _options.MinFontSize, _options.MaxFontSize);
        for (; ; candidateFontSize -= _options.FontSizeStep)
        {
            var normalized = Math.Max(_options.MinFontSize, Math.Round(candidateFontSize, 2));
            if (Fits(Measure(text.Trim(), normalized, bounds.Width), bounds))
            {
                fontSize = normalized;
                return true;
            }

            if (normalized <= _options.MinFontSize + 0.01)
                return false;
        }
    }

    /// <summary>
    /// 使用当前布局测量器确认文本在指定范围内确实可容纳。
    /// </summary>
    public bool IsTextContained(string text, double fontSize, OcrBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(text);
        return bounds.IsValid && Fits(Measure(text.Trim(), fontSize, bounds.Width), bounds);
    }

    private ScreenshotOverlayLayout LayoutOne(
        IndexedItem entry,
        int pixelWidth,
        int pixelHeight,
        List<PlacedRect> placed)
    {
        var item = entry.Item;
        var unitId = string.IsNullOrWhiteSpace(item.UnitId)
            ? $"u{entry.Index + 1:0000}"
            : item.UnitId.Trim();
        var translation = item.Translation?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(translation))
        {
            return Skipped(unitId, item, "empty_translation");
        }

        if (!item.Bounds.IsWithin(pixelWidth, pixelHeight))
        {
            return Skipped(unitId, item, "invalid_or_out_of_bounds");
        }

        var baseBounds = EnsureMinimumBounds(item.Bounds, pixelWidth, pixelHeight);
        var candidates = BuildCandidates(baseBounds, pixelWidth, pixelHeight, placed, translation);
        ScreenshotOverlayLayout? textFitCandidate = null;
        foreach (var candidate in candidates)
        {
            for (var fontSize = PreferredFontSize(baseBounds.Height); ; fontSize -= _options.FontSizeStep)
            {
                var normalizedFontSize = Math.Max(_options.MinFontSize, Math.Round(fontSize, 2));
                var measured = Measure(translation, normalizedFontSize, candidate.Width);
                if (Fits(measured, candidate))
                {
                    var status = candidate == item.Bounds && normalizedFontSize >= PreferredFontSize(baseBounds.Height) - 0.01
                        ? ScreenshotOverlayLayoutStatus.Placed
                        : ScreenshotOverlayLayoutStatus.Degraded;
                    var reason = status == ScreenshotOverlayLayoutStatus.Degraded
                        ? DescribeDegradation(candidate, item.Bounds, normalizedFontSize, baseBounds.Height)
                        : null;
                    var result = new ScreenshotOverlayLayout(
                        unitId,
                        item.Bounds,
                        candidate,
                        translation,
                        normalizedFontSize,
                        measured.LineCount,
                        measured.Width,
                        measured.Height,
                        status,
                        reason,
                        item.Polygon);

                    if (IntersectsPlaced(candidate, placed))
                    {
                        // Keep the first text-fitting candidate as a deterministic
                        // degraded fallback when every readable position collides.
                        textFitCandidate ??= result;
                    }
                    else
                    {
                        placed.Add(new PlacedRect(candidate, entry.Index));
                        return result;
                    }
                }

                if (normalizedFontSize <= _options.MinFontSize + 0.01)
                    break;
            }
        }

        if (textFitCandidate is not null)
        {
            var degraded = textFitCandidate with
            {
                Status = ScreenshotOverlayLayoutStatus.Degraded,
                DegradationReason = AppendReason(textFitCandidate.DegradationReason, "collision_unavoidable")
            };
            placed.Add(new PlacedRect(degraded.LayoutBounds, entry.Index));
            return degraded;
        }

        // Never return a box whose measured text is larger than the image. The
        // overlay root clips to the screenshot bounds, so such a result would
        // silently truncate the translation.
        return Skipped(unitId, item, "text_does_not_fit_within_image");
    }

    private double PreferredFontSize(int sourceHeight) =>
        Math.Clamp(sourceHeight * _options.PreferredFontSizeRatio, _options.MinFontSize, _options.MaxFontSize);

    private static double ConfidencePriority(double? confidence) =>
        confidence is { } value && double.IsFinite(value)
            ? value
            : double.NegativeInfinity;

    private static ScreenshotOverlayLayout Skipped(
        string unitId,
        ScreenshotOverlayItem item,
        string reason) =>
        new(
            unitId,
            item.Bounds,
            default,
            item.Translation?.Trim() ?? string.Empty,
            0,
            0,
            0,
            0,
            ScreenshotOverlayLayoutStatus.Skipped,
            reason,
            item.Polygon);

    private OcrBounds EnsureMinimumBounds(OcrBounds bounds, int pixelWidth, int pixelHeight)
    {
        var width = Math.Min(pixelWidth, Math.Max(bounds.Width, _options.MinimumBoxWidth));
        var height = Math.Min(pixelHeight, Math.Max(bounds.Height, _options.MinimumBoxHeight));
        var x = Math.Clamp(bounds.X, 0, pixelWidth - width);
        var y = Math.Clamp(bounds.Y, 0, pixelHeight - height);
        return new OcrBounds(x, y, width, height);
    }

    private IEnumerable<OcrBounds> BuildCandidates(
        OcrBounds baseBounds,
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<PlacedRect> placed,
        string translation)
    {
        var candidates = new List<OcrBounds>
        {
            baseBounds,
        };

        // First try to move the original-sized card into nearby free space. This
        // keeps the overlay local to the OCR block and avoids expanding a small
        // block across the entire screenshot merely to resolve a collision.
        AddPositions(candidates, baseBounds.Width, baseBounds.Height, baseBounds, pixelWidth, pixelHeight, placed);

        // Determine a tight readable size at the minimum font before trying broad
        // image-edge expansions. This prevents a short translation from turning a
        // 100x20 OCR block into a full-height card just because the original box
        // was too short for wrapping.
        var widthAtImage = Measure(translation, _options.MinFontSize, pixelWidth).Width;
        var desiredWidth = ClampDimension(widthAtImage, baseBounds.Width, pixelWidth);
        foreach (var width in new[] { baseBounds.Width, desiredWidth })
        {
            var requiredHeight = Measure(translation, _options.MinFontSize, width).Height;
            var desiredHeight = ClampDimension(requiredHeight, baseBounds.Height, pixelHeight);
            foreach (var height in new[] { baseBounds.Height, desiredHeight })
                AddPositions(candidates, width, height, baseBounds, pixelWidth, pixelHeight, placed);
        }

        // Last-resort full-image card. It is considered only after tight local
        // candidates and is still subject to collision/degradation reporting.
        AddCandidate(candidates, new OcrBounds(0, 0, pixelWidth, pixelHeight), pixelWidth, pixelHeight);

        return candidates
            .Where(candidate => candidate.IsWithin(pixelWidth, pixelHeight))
            .Distinct()
            .ToArray();
    }

    private void AddPositions(
        ICollection<OcrBounds> candidates,
        int width,
        int height,
        OcrBounds source,
        int pixelWidth,
        int pixelHeight,
        IReadOnlyList<PlacedRect> placed)
    {
        AddCandidate(candidates, new OcrBounds(source.X, source.Y, width, height), pixelWidth, pixelHeight);
        AddCandidate(candidates, new OcrBounds(0, source.Y, width, height), pixelWidth, pixelHeight);
        AddCandidate(candidates, new OcrBounds(pixelWidth - width, source.Y, width, height), pixelWidth, pixelHeight);
        AddCandidate(candidates, new OcrBounds(source.X, 0, width, height), pixelWidth, pixelHeight);
        AddCandidate(candidates, new OcrBounds(source.X, pixelHeight - height, width, height), pixelWidth, pixelHeight);
        foreach (var existing in placed)
        {
            AddCandidate(candidates, new OcrBounds(existing.Bounds.Right + _options.CollisionGap, source.Y, width, height), pixelWidth, pixelHeight);
            AddCandidate(candidates, new OcrBounds(existing.Bounds.X - _options.CollisionGap - width, source.Y, width, height), pixelWidth, pixelHeight);
            AddCandidate(candidates, new OcrBounds(source.X, existing.Bounds.Bottom + _options.CollisionGap, width, height), pixelWidth, pixelHeight);
            AddCandidate(candidates, new OcrBounds(source.X, existing.Bounds.Y - _options.CollisionGap - height, width, height), pixelWidth, pixelHeight);
        }
    }

    private static int ClampDimension(double measured, int minimum, int maximum) =>
        (int)Math.Clamp(Math.Ceiling(measured), minimum, maximum);

    private static void AddCandidate(
        ICollection<OcrBounds> candidates,
        OcrBounds candidate,
        int pixelWidth,
        int pixelHeight)
    {
        if (candidate.IsWithin(pixelWidth, pixelHeight) && !candidates.Contains(candidate))
            candidates.Add(candidate);
    }

    private bool IntersectsPlaced(OcrBounds candidate, IReadOnlyList<PlacedRect> placed)
    {
        foreach (var existing in placed)
        {
            if (Intersects(candidate, existing.Bounds, _options.CollisionGap))
                return true;
        }

        return false;
    }

    private static bool Intersects(OcrBounds first, OcrBounds second, int gap)
    {
        var firstLeft = (long)first.X - gap;
        var firstTop = (long)first.Y - gap;
        var firstRight = (long)first.X + first.Width + gap;
        var firstBottom = (long)first.Y + first.Height + gap;
        var secondLeft = (long)second.X;
        var secondTop = (long)second.Y;
        var secondRight = (long)second.X + second.Width;
        var secondBottom = (long)second.Y + second.Height;
        return firstLeft < secondRight && firstRight > secondLeft &&
               firstTop < secondBottom && firstBottom > secondTop;
    }

    private bool Fits(OverlayTextMeasurement measured, OcrBounds bounds) =>
        measured.Width <= bounds.Width + 0.01 && measured.Height <= bounds.Height + 0.01;

    private OverlayTextMeasurement Measure(string text, double fontSize, int candidateWidth) =>
        _measure(text, fontSize, candidateWidth);

    private OverlayTextMeasurement MeasureApproximate(
        string text,
        double fontSize,
        int candidateWidth)
    {
        var contentWidth = Math.Max(1, candidateWidth - _options.HorizontalPadding);
        var lineHeight = fontSize * _options.LineHeightRatio;
        var lines = 0;
        var widest = 0d;
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var currentWidth = 0d;
            var paragraphLines = 1;
            foreach (var character in paragraph)
            {
                var characterWidth = CharacterWidth(character, fontSize);
                if (currentWidth > 0 && currentWidth + characterWidth > contentWidth)
                {
                    widest = Math.Max(widest, currentWidth);
                    currentWidth = 0;
                    paragraphLines++;
                }

                currentWidth += characterWidth;
            }

            widest = Math.Max(widest, currentWidth);
            lines += paragraphLines;
        }

        lines = Math.Max(lines, 1);
        return new OverlayTextMeasurement(
            widest + _options.HorizontalPadding,
            lines * lineHeight + _options.VerticalPadding,
            lines);
    }

    private static double CharacterWidth(char character, double fontSize)
    {
        if (char.IsWhiteSpace(character))
            return fontSize * 0.34;
        if (char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.OtherSymbol)
            return fontSize;
        if (IsWideCharacter(character))
            return fontSize;
        if (char.IsPunctuation(character))
            return fontSize * 0.55;
        return fontSize * 0.58;
    }

    private static bool IsWideCharacter(char character) =>
        character >= '\u2E80' ||
        (character >= '\u1100' && character <= '\u11FF') ||
        (character >= '\uAC00' && character <= '\uD7AF');

    private string DescribeDegradation(
        OcrBounds candidate,
        OcrBounds source,
        double fontSize,
        int sourceHeight)
    {
        var reasons = new List<string>(2);
        if (candidate != source)
            reasons.Add("expanded_or_moved");
        if (fontSize < PreferredFontSize(sourceHeight) - 0.01)
            reasons.Add("font_reduced");
        return string.Join(',', reasons);
    }

    private static string AppendReason(string? existing, string reason) =>
        string.IsNullOrWhiteSpace(existing) ? reason : $"{existing},{reason}";

    private readonly record struct IndexedItem(ScreenshotOverlayItem Item, int Index);

    private readonly record struct PlacedRect(OcrBounds Bounds, int Index);

}

/// <summary>覆盖层文本测量结果，宽高均以物理像素表达。</summary>
public readonly record struct OverlayTextMeasurement(
    double Width,
    double Height,
    int LineCount);
