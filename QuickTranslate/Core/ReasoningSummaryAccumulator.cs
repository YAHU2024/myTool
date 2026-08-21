using System.Text;

namespace QuickTranslate.Core;

/// <summary>
/// Bounds provider reasoning before it reaches the UI. The cap is measured in
/// Unicode scalar values so a partial surrogate pair is never emitted.
/// </summary>
internal sealed class ReasoningSummaryAccumulator
{
    internal const int MaxRunes = 8_000;

    private readonly StringBuilder _buffer = new();
    private int _runeCount;

    public bool IsTruncated { get; private set; }

    public string Append(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsTruncated)
            return string.Empty;

        var remaining = MaxRunes - _runeCount;
        if (remaining <= 0)
        {
            IsTruncated = true;
            return string.Empty;
        }

        var accepted = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (_runeCount >= MaxRunes)
            {
                IsTruncated = true;
                break;
            }

            _buffer.Append(rune.ToString());
            accepted.Append(rune.ToString());
            _runeCount++;
        }

        return accepted.ToString();
    }

    public string Snapshot() => _buffer.ToString();
}
