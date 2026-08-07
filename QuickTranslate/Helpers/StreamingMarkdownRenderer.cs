using System.Diagnostics;
using System.Text;
using System.Windows.Documents;

namespace QuickTranslate.Helpers;

internal sealed record StreamingMarkdownRenderStats(
    int FrameCount,
    double AverageRenderDurationMs,
    double MaxRenderDurationMs,
    long AllocatedBytes,
    long ParsedCharacters)
{
    public static StreamingMarkdownRenderStats Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Incrementally renders an append-only Markdown stream. Completed source blocks remain attached
/// to one FlowDocument; only the syntactically unsettled tail is parsed and replaced per update.
/// </summary>
internal sealed class StreamingMarkdownRenderer
{
    private readonly double _fontSize;
    private readonly int _maxDisplayCharacters;
    private readonly FlowDocument? _activeDocument;
    private readonly List<Block> _activeBlocks = [];
    private readonly StringBuilder _pendingSource = new();
    private string _rawText = string.Empty;
    private string _displayedRawText = string.Empty;
    private Run? _activePlainRun;
    private int _renderFrameCount;
    private double _totalRenderDurationMs;
    private double _maxRenderDurationMs;
    private long _allocatedBytes;

    public StreamingMarkdownRenderer(
        double fontSize,
        int maxDisplayCharacters,
        bool separateActiveDocument = false)
    {
        if (fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (maxDisplayCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDisplayCharacters));

        _fontSize = fontSize;
        _maxDisplayCharacters = maxDisplayCharacters;
        Document = MarkdownRenderer.CreateDocument(fontSize);
        if (separateActiveDocument)
            _activeDocument = MarkdownRenderer.CreateDocument(fontSize);
    }

    public FlowDocument Document { get; }

    public FlowDocument? ActiveDocument => _activeDocument;

    public bool HasStableBlocks => Document.Blocks.Count > (_activeDocument is null ? _activeBlocks.Count : 0);

    public bool HasActiveBlocks => _activeBlocks.Count > 0;

    public bool IsCollapsed { get; private set; }

    internal int CommittedCharacterCount { get; private set; }

    internal int ActiveCharacterCount => _pendingSource.Length;

    internal long ParsedCharacterCount { get; private set; }

    public bool Update(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var startedAt = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            return UpdateCore(rawText);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            _renderFrameCount++;
            _totalRenderDurationMs += durationMs;
            _maxRenderDurationMs = Math.Max(_maxRenderDurationMs, durationMs);
            _allocatedBytes += Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

    internal StreamingMarkdownRenderStats GetStats() => new(
        _renderFrameCount,
        _renderFrameCount == 0 ? 0 : _totalRenderDurationMs / _renderFrameCount,
        _maxRenderDurationMs,
        _allocatedBytes,
        ParsedCharacterCount);

    private bool UpdateCore(string rawText)
    {

        if (!rawText.StartsWith(_rawText, StringComparison.Ordinal))
            Reset();

        _rawText = rawText;
        var displayedRawText = GetDisplayedRawText(rawText);
        if (displayedRawText == _displayedRawText)
            return true;

        if (!displayedRawText.StartsWith(_displayedRawText, StringComparison.Ordinal))
            ResetDocumentState();

        var appended = displayedRawText[_displayedRawText.Length..];
        var candidateTail = _pendingSource.Append(appended).ToString();
        var commitLength = FindStablePrefixLength(candidateTail);
        var stableSource = candidateTail[..commitLength];
        var activeSource = candidateTail[commitLength..];

        if (stableSource.Length == 0 &&
            _activePlainRun is not null &&
            IsSimplePlainTextTail(activeSource))
        {
            _activePlainRun.Text = activeSource;
            CommitSourceState(activeSource, displayedRawText, stableCharacterCount: 0);
            return true;
        }

        if (!TryRenderFragment(stableSource, isFinal: true, out var stableBlocks))
        {
            return false;
        }

        IReadOnlyList<Block> activeBlocks;
        Run? activePlainRun = null;
        if (IsSimplePlainTextTail(activeSource))
        {
            activePlainRun = new Run(activeSource);
            activeBlocks = [new Paragraph(activePlainRun) { Margin = new System.Windows.Thickness(0, 2, 0, 7) }];
        }
        else if (!TryRenderFragment(activeSource, isFinal: false, out activeBlocks))
        {
            return false;
        }

        RemoveActiveBlocks();
        foreach (var block in stableBlocks)
            Document.Blocks.Add(block);
        var activeTarget = _activeDocument?.Blocks ?? Document.Blocks;
        foreach (var block in activeBlocks)
        {
            activeTarget.Add(block);
            _activeBlocks.Add(block);
        }
        _activePlainRun = activePlainRun;

        CommitSourceState(activeSource, displayedRawText, stableSource.Length);
        return true;
    }

    private void CommitSourceState(
        string activeSource,
        string displayedRawText,
        int stableCharacterCount)
    {
        CommittedCharacterCount += stableCharacterCount;
        _pendingSource.Clear();
        _pendingSource.Append(activeSource);
        _displayedRawText = displayedRawText;
    }

    internal static bool IsSimplePlainTextTail(string source)
    {
        if (source.Length == 0)
            return false;
        if (source.StartsWith("- ", StringComparison.Ordinal) ||
            source.StartsWith("+ ", StringComparison.Ordinal) ||
            source.StartsWith("---", StringComparison.Ordinal) ||
            source.Contains("://", StringComparison.Ordinal) ||
            source.Contains('@') ||
            source.Contains("++", StringComparison.Ordinal) ||
            source.Contains("==", StringComparison.Ordinal) ||
            source.Contains('^') ||
            StartsWithOrderedListMarker(source))
        {
            return false;
        }

        foreach (var character in source)
        {
            if (character is '\r' or '\n' or '`' or '*' or '_' or '[' or ']' or '#' or '>' or '|' or '~' or '<' or '\\' or '&')
                return false;
        }

        return true;
    }

    private static bool StartsWithOrderedListMarker(string source)
    {
        var index = 0;
        while (index < source.Length && char.IsDigit(source[index]))
            index++;
        return index > 0 &&
            index + 1 < source.Length &&
            source[index] is '.' or ')' &&
            source[index + 1] == ' ';
    }

    private string GetDisplayedRawText(string rawText)
    {
        IsCollapsed = rawText.Length > _maxDisplayCharacters;
        if (!IsCollapsed)
            return rawText;

        var length = _maxDisplayCharacters;
        if (length > 0 && length < rawText.Length &&
            char.IsHighSurrogate(rawText[length - 1]) && char.IsLowSurrogate(rawText[length]))
        {
            length--;
        }

        return rawText[..length];
    }

    private bool TryRenderFragment(string source, bool isFinal, out IReadOnlyList<Block> blocks)
    {
        if (source.Length == 0)
        {
            blocks = Array.Empty<Block>();
            return true;
        }

        ParsedCharacterCount += source.Length;
        return MarkdownRenderer.TryRenderBlocks(source, _fontSize, isFinal, out blocks);
    }

    private void Reset()
    {
        _rawText = string.Empty;
        ResetDocumentState();
    }

    private void ResetDocumentState()
    {
        Document.Blocks.Clear();
        _activeDocument?.Blocks.Clear();
        _activeBlocks.Clear();
        _pendingSource.Clear();
        _displayedRawText = string.Empty;
        _activePlainRun = null;
        CommittedCharacterCount = 0;
        ParsedCharacterCount = 0;
        _renderFrameCount = 0;
        _totalRenderDurationMs = 0;
        _maxRenderDurationMs = 0;
        _allocatedBytes = 0;
    }

    private void RemoveActiveBlocks()
    {
        var activeTarget = _activeDocument?.Blocks ?? Document.Blocks;
        foreach (var block in _activeBlocks)
            activeTarget.Remove(block);
        _activeBlocks.Clear();
        _activePlainRun = null;
    }

    internal static int FindStablePrefixLength(string source)
    {
        var stableEnd = 0;
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var lineStart = 0;

        while (lineStart < source.Length)
        {
            var lineFeed = source.IndexOf('\n', lineStart);
            if (lineFeed < 0)
                break;

            var lineLength = lineFeed - lineStart;
            if (lineLength > 0 && source[lineFeed - 1] == '\r')
                lineLength--;
            var line = source.AsSpan(lineStart, lineLength);

            if (TryReadFence(line, out var marker, out var markerLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceCharacter = marker;
                    fenceLength = markerLength;
                }
                else if (marker == fenceCharacter && markerLength >= fenceLength && IsClosingFence(line, markerLength))
                {
                    inFence = false;
                }
            }
            else if (!inFence && line.Trim().IsEmpty)
            {
                stableEnd = lineFeed + 1;
            }

            lineStart = lineFeed + 1;
        }

        return stableEnd;
    }

    private static bool TryReadFence(ReadOnlySpan<char> line, out char marker, out int markerLength)
    {
        marker = '\0';
        markerLength = 0;
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
            offset++;
        if (offset >= line.Length || line[offset] is not ('`' or '~'))
            return false;

        marker = line[offset];
        while (offset + markerLength < line.Length && line[offset + markerLength] == marker)
            markerLength++;
        return markerLength >= 3;
    }

    private static bool IsClosingFence(ReadOnlySpan<char> line, int markerLength)
    {
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
            offset++;
        return line[(offset + markerLength)..].Trim().IsEmpty;
    }
}
