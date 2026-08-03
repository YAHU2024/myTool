using Microsoft.Data.Sqlite;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class LocalDictionaryWordLookupServiceTests
{
    [Fact]
    public async Task Lookup_ReturnsDictionaryResult_ForExactWord()
    {
        using var db = new TestDictionaryDb();
        db.InsertEcdict("hello", "həˈləʊ", "喂；你好", "an expression of greeting");
        db.InsertWordNet(
            "hello",
            "n",
            "an expression of greeting",
            "every morning they exchanged polite hellos");

        using var service = new LocalDictionaryWordLookupService(db.Path);
        var result = await service.LookupAsync(
            new WordLookupRequest("hello", "简体中文"),
            CancellationToken.None);

        Assert.Equal("hello", result.Headword);
        Assert.Equal(WordLookupSourceKind.Dictionary, result.Source.Kind);
        Assert.Contains("本地词典", result.Source.DisplayName);
        Assert.Contains("喂；你好", result.Senses[0].Definition);
        Assert.Contains("an expression of greeting", result.Senses[0].EnglishDefinition);
        Assert.Contains("every morning", result.Examples[0].Sentence);
    }

    [Fact]
    public async Task Lookup_UsesSwFallback_ForSpacedVariant()
    {
        using var db = new TestDictionaryDb();
        db.InsertEcdict("long-time", "lɒŋ taɪm", "长时间的", string.Empty);

        using var service = new LocalDictionaryWordLookupService(db.Path);
        var result = await service.LookupAsync(
            new WordLookupRequest("long time", "简体中文"),
            CancellationToken.None);

        Assert.Equal("long-time", result.Headword);
        Assert.Contains("长时间的", result.Senses[0].Definition);
    }

    [Fact]
    public async Task Lookup_PrefersExactWord_WhenNormalizedKeysCollide()
    {
        using var db = new TestDictionaryDb();
        db.InsertEcdict("long-time", "", "精确词条", string.Empty, bnc: 100);
        db.InsertEcdict("long time", "", "高频变体", string.Empty, bnc: 1);

        using var service = new LocalDictionaryWordLookupService(db.Path);
        var result = await service.LookupAsync(
            new WordLookupRequest("long-time", "简体中文"),
            CancellationToken.None);

        Assert.Equal("long-time", result.Headword);
        Assert.Contains("精确词条", result.Senses[0].Definition);
    }

    [Fact]
    public async Task Lookup_UsesWordNetForm_WhenInflectedWordHasNoEcdictEntry()
    {
        using var db = new TestDictionaryDb();
        db.InsertWordNet(
            "take",
            "v",
            "to move or carry something",
            "I take a book to school.");
        db.InsertWordNetForm("taken", "take");

        using var service = new LocalDictionaryWordLookupService(db.Path);
        var result = await service.LookupAsync(
            new WordLookupRequest("taken", "简体中文"),
            CancellationToken.None);

        Assert.Equal("take", result.Headword);
        Assert.Contains("to move or carry something", result.Senses[0].Definition);
        Assert.Equal(WordLookupSourceKind.Dictionary, result.Source.Kind);
    }

    [Fact]
    public async Task Lookup_SplitsEcdictTranslationLines_IntoMultipleSenses()
    {
        using var db = new TestDictionaryDb();
        db.InsertEcdict(
            "run",
            "rʌn",
            "n. 跑；奔跑\nvi. 跑；跑步",
            string.Empty);

        using var service = new LocalDictionaryWordLookupService(db.Path);
        var result = await service.LookupAsync(
            new WordLookupRequest("run", "简体中文"),
            CancellationToken.None);

        Assert.Equal(2, result.Senses.Count);
        Assert.Equal("noun", result.Senses[0].PartOfSpeech);
        Assert.Equal("verb", result.Senses[1].PartOfSpeech);
        Assert.Contains("跑", result.Senses[0].Definition);
        Assert.Contains("跑", result.Senses[1].Definition);
    }

    [Fact]
    public async Task Lookup_ThrowsNotFound_WhenNeitherSourceHasEntry()
    {
        using var db = new TestDictionaryDb();
        using var service = new LocalDictionaryWordLookupService(db.Path);

        await Assert.ThrowsAsync<WordLookupNotFoundException>(() =>
            service.LookupAsync(
                new WordLookupRequest("chatgpt", "简体中文"),
                CancellationToken.None));
    }

    private sealed class TestDictionaryDb : IDisposable
    {
        public TestDictionaryDb()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"word-dict-test-{Guid.NewGuid():N}.db");
            using var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE ecdict_entries (
                    word TEXT NOT NULL,
                    phonetic TEXT,
                    definition TEXT,
                    translation TEXT,
                    bnc INTEGER,
                    frq INTEGER,
                    sw TEXT NOT NULL
                );
                CREATE INDEX idx_ecdict_sw ON ecdict_entries(sw);
                CREATE TABLE wordnet_senses (
                    lemma TEXT NOT NULL COLLATE NOCASE,
                    pos TEXT,
                    definition TEXT,
                    example TEXT
                );
                CREATE INDEX idx_wordnet_lemma ON wordnet_senses(lemma);
                CREATE TABLE wordnet_forms (
                    form TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    lemma TEXT NOT NULL COLLATE NOCASE
                );
                """;
            command.ExecuteNonQuery();
        }

        public string Path { get; }

        public void InsertEcdict(
            string word,
            string phonetic,
            string translation,
            string definition,
            int? bnc = null)
        {
            Execute(
                """
                INSERT INTO ecdict_entries
                    (word, phonetic, definition, translation, bnc, sw)
                VALUES
                    (@word, @phonetic, @definition, @translation, @bnc, @sw)
                """,
                ("@word", word),
                ("@phonetic", phonetic),
                ("@definition", definition),
                ("@translation", translation),
                ("@bnc", bnc ?? (object)DBNull.Value),
                ("@sw", NormalizeKey(word)));
        }

        public void InsertWordNet(
            string lemma,
            string pos,
            string definition,
            string example)
        {
            Execute(
                """
                INSERT INTO wordnet_senses
                    (lemma, pos, definition, example)
                VALUES
                    (@lemma, @pos, @definition, @example)
                """,
                ("@lemma", lemma),
                ("@pos", pos),
                ("@definition", definition),
                ("@example", example));
        }

        public void InsertWordNetForm(string form, string lemma)
        {
            Execute(
                "INSERT INTO wordnet_forms (form, lemma) VALUES (@form, @lemma)",
                ("@form", form),
                ("@lemma", lemma));
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private static string NormalizeKey(string word) =>
            new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
