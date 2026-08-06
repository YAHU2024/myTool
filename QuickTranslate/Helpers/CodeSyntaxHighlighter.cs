using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ColorCode;
using ColorCode.Common;
using ColorCode.Compilation;
using ColorCode.Parsing;
using ColorCode.Styling;

namespace QuickTranslate.Helpers;

/// <summary>
/// Converts ColorCode scopes into WPF runs without changing the source text used for copy.
/// Unknown languages and parser failures intentionally fall back to the normal code font.
/// </summary>
internal static class CodeSyntaxHighlighter
{
    internal const int MaxHighlightedCharacters = 50_000;

    private static readonly Dictionary<string, string> LanguageAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "c#",
            ["csharp"] = "c#",
            ["js"] = "javascript",
            ["jscript"] = "javascript",
            ["ts"] = "typescript",
            ["py"] = "python",
            ["py3"] = "python",
            ["python3"] = "python",
            ["ps1"] = "powershell",
            ["pwsh"] = "powershell",
            ["c++"] = "cpp",
            ["cplusplus"] = "cpp",
            ["md"] = "markdown",
            ["html5"] = "html",
            ["xhtml"] = "html"
        };

    private static readonly StyleDictionary DarkStyles = StyleDictionary.DefaultDark;
    private static readonly LanguageParser Parser = CreateParser();
    private static readonly object ParserSync = new();
    private static readonly Brush DefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly ConcurrentDictionary<string, Brush> ScopeBrushes =
        new(StringComparer.Ordinal);

    public static bool TryHighlight(TextBlock target, string code, string? language)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length > MaxHighlightedCharacters)
            return false;

        var resolvedLanguage = ResolveLanguage(language);
        if (resolvedLanguage is null)
            return false;

        try
        {
            target.Inlines.Clear();
            lock (ParserSync)
                Parser.Parse(code, resolvedLanguage, (text, scopes) => AppendRuns(target.Inlines, text, scopes));
            return target.Inlines.Count > 0;
        }
        catch (Exception)
        {
            target.Inlines.Clear();
            target.Text = code;
            return false;
        }
    }

    private static ILanguage? ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var id = NormalizeLanguageInfo(language);
        if (id is null)
            return null;
        if (LanguageAliases.TryGetValue(id, out var alias))
            id = alias;

        return Languages.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) ||
            candidate.HasAlias(id));
    }

    private static string? NormalizeLanguageInfo(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var token = language.Trim();
        if (token.StartsWith("{.", StringComparison.Ordinal) && token.EndsWith('}'))
            token = token[2..^1];
        else if (token.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
            token = token[9..];

        var separator = token.IndexOfAny([' ', '\t', '\r', '\n', ',']);
        if (separator >= 0)
            token = token[..separator];

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static void AppendRuns(
        InlineCollection target,
        string text,
        IList<Scope> scopes)
    {
        if (text.Length == 0)
            return;

        var boundaries = new SortedSet<int> { 0, text.Length };
        foreach (var scope in scopes)
        {
            var start = Math.Clamp(scope.Index, 0, text.Length);
            var end = Math.Clamp(scope.Index + scope.Length, start, text.Length);
            boundaries.Add(start);
            boundaries.Add(end);
        }

        var points = boundaries.ToArray();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var start = points[i];
            var length = points[i + 1] - start;
            if (length <= 0)
                continue;

            var scope = scopes
                .Where(candidate => candidate.Index <= start &&
                                     candidate.Index + candidate.Length >= start + length)
                .OrderBy(candidate => candidate.Length)
                .FirstOrDefault();
            var run = new Run(text.Substring(start, length))
            {
                Foreground = GetBrush(scope?.Name)
            };
            if (scope?.Name is { } scopeName && DarkStyles[scopeName] is { } style)
            {
                run.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
                run.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
            }
            target.Add(run);
        }
    }

    private static Brush GetBrush(string? scopeName)
    {
        if (scopeName is null)
            return DefaultBrush;

        return ScopeBrushes.GetOrAdd(scopeName, CreateBrush);
    }

    private static Brush CreateBrush(string scopeName)
    {
        if (DarkStyles[scopeName] is not { Foreground: { } foreground })
            return DefaultBrush;

        try
        {
            if (ColorConverter.ConvertFromString(foreground) is Color color)
                return Freeze(new SolidColorBrush(color));
        }
        catch (FormatException)
        {
        }

        return DefaultBrush;
    }

    private static LanguageParser CreateParser()
    {
        var compiled = new Dictionary<string, CompiledLanguage>(StringComparer.OrdinalIgnoreCase);
        var languages = Languages.All.ToDictionary(language => language.Id, StringComparer.OrdinalIgnoreCase);
        return new LanguageParser(
            new LanguageCompiler(compiled, new System.Threading.ReaderWriterLockSlim()),
            new LanguageRepository(languages));
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        if (value.CanFreeze)
            value.Freeze();
        return value;
    }
}
