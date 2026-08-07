using System.Text;
using System.Windows.Documents;

namespace QuickTranslate.Helpers;

/// <summary>
/// Incrementally renders an append-only Markdown stream. Completed source blocks remain attached
/// to one FlowDocument; only the syntactically unsettled tail is parsed and replaced per update.
/// </summary>
internal sealed class StreamingMarkdownRenderer
{
    private readonly double _fontSize;
    private readonly int _maxDisplayCharacters;
    private readonly List<Block> _activeBlocks = [];
    private readonly StringBuilder _pendingSource = new();
    private string _rawText = string.Empty;
    private string _displayedRawText = string.Empty;

    public StreamingMarkdownRenderer(double fontSize, int maxDisplayCharacters)
    {
        if (fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (maxDisplayCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDisplayCharacters));

        _fontSize = fontSize;
        _maxDisplayCharacters = maxDisplayCharacters;
        Document = MarkdownRenderer.CreateDocument(fontSize);
    }

    public FlowDocument Document { get; }

    public bool IsCollapsed { get; private set; }

    internal int CommittedCharacterCount { get; private set; }

    internal int ActiveCharacterCount => _pendingSource.Length;

    internal long ParsedCharacterCount { get; private set; }

    public bool Update(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        if (!rawText.StartsWith(_rawText, StringComparison.Ordinal))
            Reset();

        _rawText = rawText;
        var displayedRawText = GetDisplayedRawText(rawText);
        if (displayedRawText == _displayedRawText)
            return true;

        if (!displayedRawText.StartsWith(_displayedRawText, StringComparison.Ordinal))
            ResetDocumentState();

        var appended = displayedRawText[_displayedRawText.Length..];
        var candidateTail = _pendingSource.ToString() + appended;
        var commitLength = FindStablePrefixLength(candidateTail);
        var stableSource = candidateTail[..commitLength];
        var activeSource = candidateTail[commitLength..];

        if (!TryRenderFragment(stableSource, isFinal: true, out var stableBlocks) ||
            !TryRenderFragment(activeSource, isFinal: false, out var activeBlocks))
        {
            return false;
        }

        RemoveActiveBlocks();
        foreach (var block in stableBlocks)
            Document.Blocks.Add(block);
        foreach (var block in activeBlocks)
        {
            Document.Blocks.Add(block);
            _activeBlocks.Add(block);
        }

        CommittedCharacterCount += stableSource.Length;
        _pendingSource.Clear();
        _pendingSource.Append(activeSource);
        _displayedRawText = displayedRawText;
        return true;
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
        _activeBlocks.Clear();
        _pendingSource.Clear();
        _displayedRawText = string.Empty;
        CommittedCharacterCount = 0;
        ParsedCharacterCount = 0;
    }

    private void RemoveActiveBlocks()
    {
        foreach (var block in _activeBlocks)
            Document.Blocks.Remove(block);
        _activeBlocks.Clear();
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
