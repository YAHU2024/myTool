using System.Text;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

/// <summary>
/// 归一化 Windows OCR 常见的空白噪声。只删除 CJK 字符之间的伪空格，
/// 不删除拉丁词之间的真实分隔。
/// </summary>
public static class OcrTextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var collapsed = CollapseWhitespace(text);
        if (collapsed.Length == 0)
            return string.Empty;

        var runes = collapsed.EnumerateRunes().ToArray();
        var builder = new StringBuilder(collapsed.Length);
        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            if (!Rune.IsWhiteSpace(rune))
            {
                builder.Append(rune);
                continue;
            }

            var previous = PreviousNonWhitespace(runes, index);
            var next = NextNonWhitespace(runes, index);
            if (previous is null || next is null)
                continue;

            if (ShouldRemoveBetween(previous.Value, next.Value))
                continue;

            builder.Append(' ');
        }

        return builder.ToString().Trim();
    }

    public static string Join(IEnumerable<string> texts, string separator = "\n")
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(separator);
        return string.Join(
            separator,
            texts.Select(Normalize).Where(static text => text.Length > 0));
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            builder.Append(rune);
            pendingSpace = false;
        }

        return builder.ToString();
    }

    private static Rune? PreviousNonWhitespace(IReadOnlyList<Rune> runes, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!Rune.IsWhiteSpace(runes[i]))
                return runes[i];
        }

        return null;
    }

    private static Rune? NextNonWhitespace(IReadOnlyList<Rune> runes, int index)
    {
        for (var i = index + 1; i < runes.Count; i++)
        {
            if (!Rune.IsWhiteSpace(runes[i]))
                return runes[i];
        }

        return null;
    }

    private static bool ShouldRemoveBetween(Rune previous, Rune next)
    {
        if (IsCjk(previous) && IsCjk(next))
            return true;
        if (IsCjkPunctuation(previous) || IsCjkPunctuation(next))
            return true;
        return false;
    }

    private static bool IsCjk(Rune rune) =>
        IsHan(rune.Value) ||
        rune.Value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF ||
        rune.Value is >= 0xAC00 and <= 0xD7AF;

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2EBEF;

    private static bool IsCjkPunctuation(Rune rune) =>
        rune.Value is '。' or '，' or '：' or '；' or '！' or '？' or '、' or '．' or
            '（' or '）' or '【' or '】' or '「' or '」' or '『' or '』' or '《' or '》' or
            '〈' or '〉' or '“' or '”' or '‘' or '’';
}
