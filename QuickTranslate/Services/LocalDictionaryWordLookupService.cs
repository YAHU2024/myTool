using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class LocalDictionaryWordLookupService : IWordLookupService, IDisposable
{
    private const int MaxSenses = 6;
    private const int MaxExamples = 3;
    private const int MaxPronunciations = 2;
    private readonly string _databasePath;

    public LocalDictionaryWordLookupService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(_databasePath))
            throw new FileNotFoundException("Word dictionary database was not found.", _databasePath);
    }

    public async Task<WordLookupResult> LookupAsync(
        WordLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = WordLookupPromptBuilder.NormalizeQuery(request.Query);
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("WordLookupService", "lookup.started", new
        {
            query_scalars = query.EnumerateRunes().Count(),
            provider = "ecdict-oewn-local"
        });

        var result = await Task.Run(
            () => LookupCore(query, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Logger.Info("WordLookupService", "lookup.completed", new
        {
            provider = result.Source.ProviderId,
            senses = result.Senses.Count,
            examples = result.Examples.Count,
            collocations = result.Collocations.Count,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
        });
        return result;
    }

    private WordLookupResult LookupCore(string query, CancellationToken cancellationToken)
    {
        using var connection = OpenReadOnlyConnection();
        var ecdict = QueryEcdict(connection, query);
        var wordnet = QueryWordNet(connection, query);
        cancellationToken.ThrowIfCancellationRequested();

        if (ecdict is null && wordnet.Count == 0)
            throw new WordLookupNotFoundException();

        var headword = ecdict?.Word
            ?? wordnet.FirstOrDefault()?.Lemma
            ?? query;
        var pronunciations = BuildPronunciations(ecdict);
        var senses = BuildSenses(ecdict, wordnet);
        var examples = BuildExamples(wordnet);

        return new WordLookupResult(
            headword,
            pronunciations,
            senses,
            examples,
            Array.Empty<string>(),
            new WordLookupSource(
                "ecdict-oewn-local",
                "本地词典 · ECDICT + OEWN",
                WordLookupSourceKind.Dictionary));
    }

    public void Dispose()
    {
        // Connections are opened per lookup and disposed by their using scopes.
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static EcdictEntry? QueryEcdict(
        SqliteConnection connection,
        string word)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT word, phonetic, translation, definition
            FROM ecdict_entries
            WHERE sw = @sw
            ORDER BY
                CASE WHEN word = @word COLLATE NOCASE THEN 0 ELSE 1 END,
                COALESCE(bnc, 999999999),
                COALESCE(frq, 999999999),
                word
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@word", word);
        command.Parameters.AddWithValue("@sw", NormalizeKey(word));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEcdict(reader) : null;
    }

    private static IReadOnlyList<WordNetSense> QueryWordNet(
        SqliteConnection connection,
        string word)
    {
        var senses = QuerySensesByLemma(connection, word);
        if (senses.Count > 0)
            return senses;

        string? lemma = null;
        using (var form = connection.CreateCommand())
        {
            form.CommandText =
                """
                SELECT lemma
                FROM wordnet_forms
                WHERE form = @form COLLATE NOCASE
                LIMIT 1
                """;
            form.Parameters.AddWithValue("@form", word);
            using var reader = form.ExecuteReader();
            if (reader.Read())
                lemma = reader.GetString(0);
        }

        return lemma is null
            ? Array.Empty<WordNetSense>()
            : QuerySensesByLemma(connection, lemma);
    }

    private static IReadOnlyList<WordNetSense> QuerySensesByLemma(
        SqliteConnection connection,
        string lemma)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lemma, pos, definition, example
            FROM wordnet_senses
            WHERE lemma = @lemma COLLATE NOCASE
            ORDER BY pos, definition
            LIMIT 24
            """;
        command.Parameters.AddWithValue("@lemma", lemma);

        var senses = new List<WordNetSense>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            senses.Add(new WordNetSense(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return senses;
    }

    private static EcdictEntry ReadEcdict(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.IsDBNull(3) ? string.Empty : reader.GetString(3));

    private static IReadOnlyList<WordPronunciation> BuildPronunciations(
        EcdictEntry? ecdict)
    {
        var phonetic = ecdict?.Phonetic ?? string.Empty;
        var parts = phonetic.Split(
            [';', '；', ',', '，'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts
            .Take(MaxPronunciations)
            .Select(part => new WordPronunciation(string.Empty, part))
            .ToArray();
    }

    private static IReadOnlyList<WordSense> BuildSenses(
        EcdictEntry? ecdict,
        IReadOnlyList<WordNetSense> wordnet)
    {
        var senses = new List<WordSense>(MaxSenses);
        var translationCandidates = ParseSenseLines(ecdict?.Translation);
        var translations = translationCandidates
            .Where(sense => ContainsCjk(sense.Definition))
            .ToList();
        var ecdictDefinitions = ParseSenseLines(ecdict?.Definition);
        ecdictDefinitions.AddRange(translationCandidates.Where(sense =>
            !ContainsCjk(sense.Definition)));

        for (var index = 0; index < translations.Count && senses.Count < MaxSenses; index++)
        {
            var translation = translations[index];
            var ecdictDefinition = FindSense(ecdictDefinitions, translation.Pos, index);
            var wordnetSense = FindSense(wordnet, translation.Pos, index);
            var effectivePos = translation.Pos;
            if (effectivePos.Length == 0)
                effectivePos = ecdictDefinition?.Pos ?? string.Empty;
            if (effectivePos.Length == 0)
                effectivePos = wordnetSense?.Pos ?? string.Empty;
            var english = ecdictDefinition?.Definition
                ?? wordnetSense?.Definition
                ?? string.Empty;
            senses.Add(new WordSense(
                WordPartOfSpeechNormalizer.ToDisplayLabel(effectivePos),
                translation.Definition,
                english));
        }

        if (translations.Count > 0)
            return senses;

        foreach (var item in ecdictDefinitions.Concat(
                     wordnet.Select(sense => new ParsedSense(
                         WordPartOfSpeechNormalizer.ToCanonical(sense.Pos),
                         sense.Definition))))
        {
            if (senses.Count >= MaxSenses)
                break;

            var definition = item.Definition.Trim();
            if (definition.Length == 0 ||
                senses.Any(sense => string.Equals(
                    sense.EnglishDefinition,
                    definition,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            senses.Add(new WordSense(
                WordPartOfSpeechNormalizer.ToDisplayLabel(item.Pos),
                string.Empty,
                definition));
        }

        return senses;
    }

    private static IReadOnlyList<WordExample> BuildExamples(
        IReadOnlyList<WordNetSense> wordnet)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var examples = new List<WordExample>(MaxExamples);
        foreach (var item in wordnet)
        {
            if (examples.Count >= MaxExamples)
                break;

            var sentence = item.Example.Trim();
            if (sentence.Length == 0 || !seen.Add(sentence))
                continue;

            examples.Add(new WordExample(sentence, string.Empty));
        }

        return examples;
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static (string Pos, string Definition) SplitPosLine(
        string line,
        string fallbackPos)
    {
        var dot = line.IndexOf('.');
        if (dot is > 0 and <= 6)
        {
            var prefix = line[..dot].Trim();
            if (prefix.Length > 0 && prefix.All(char.IsLetter))
            {
                return (
                    WordPartOfSpeechNormalizer.ToCanonical(prefix),
                    line[(dot + 1)..].Trim());
            }
        }

        return (WordPartOfSpeechNormalizer.ToCanonical(fallbackPos), line);
    }

    private static List<ParsedSense> ParseSenseLines(string? text) =>
        SplitLines(text)
            .Select(line => SplitPosLine(line, string.Empty))
            .Select(item => new ParsedSense(item.Pos, item.Definition))
            .ToList();

    private static bool ContainsCjk(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static ParsedSense? FindSense(
        IReadOnlyList<ParsedSense> senses,
        string pos,
        int fallbackIndex)
    {
        var canonical = WordPartOfSpeechNormalizer.ToCanonical(pos);
        return (canonical.Length > 0
                ? senses.FirstOrDefault(sense => sense.Pos == canonical)
                : null)
            ?? senses.ElementAtOrDefault(fallbackIndex);
    }

    private static WordNetSense? FindSense(
        IReadOnlyList<WordNetSense> senses,
        string pos,
        int fallbackIndex)
    {
        var canonical = WordPartOfSpeechNormalizer.ToCanonical(pos);
        return (canonical.Length > 0
                ? senses.FirstOrDefault(sense =>
                    WordPartOfSpeechNormalizer.ToCanonical(sense.Pos) == canonical)
                : null)
            ?? senses.ElementAtOrDefault(fallbackIndex);
    }

    private static string NormalizeKey(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private sealed record EcdictEntry(
        string Word,
        string Phonetic,
        string Translation,
        string Definition);

    private sealed record ParsedSense(string Pos, string Definition);

    private sealed record WordNetSense(
        string Lemma,
        string Pos,
        string Definition,
        string Example);
}
