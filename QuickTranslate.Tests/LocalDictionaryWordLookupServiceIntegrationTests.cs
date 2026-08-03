using System.IO;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class LocalDictionaryWordLookupServiceIntegrationTests
{
    /// <summary>
    /// Optional check against the generated ECDICT + OEWN database.
    /// Run with: $env:QUICKTRANSLATE_WORD_DICT_DB = "...\word-dictionary-mini.db"
    /// </summary>
    [SkippableFact]
    public async Task Lookup_ReadsGeneratedEcdictAndWordNetDatabase()
    {
        var databasePath = Environment.GetEnvironmentVariable(
            "QUICKTRANSLATE_WORD_DICT_DB");
        Skip.If(string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath),
            "Set QUICKTRANSLATE_WORD_DICT_DB to the generated dictionary database.");

        using var service = new LocalDictionaryWordLookupService(databasePath);
        var result = await service.LookupAsync(
            new WordLookupRequest("run", "简体中文"),
            CancellationToken.None);

        Assert.Equal("run", result.Headword);
        Assert.Equal(WordLookupSourceKind.Dictionary, result.Source.Kind);
        Assert.NotEmpty(result.Pronunciations);
        Assert.NotEmpty(result.Senses);
        Assert.DoesNotContain(result.Senses, sense =>
            sense.PartOfSpeech is "noun" or "verb" or "adjective" or "adverb");
        Assert.All(result.Senses.Where(sense => sense.Definition.Length > 0), sense =>
            Assert.Contains(sense.Definition, character => character is >= '\u3400' and <= '\u9fff'));
        Assert.All(result.Examples, example => Assert.Empty(example.Translation));
    }
}
