namespace QuickTranslate.Models;

public sealed record WordLookupRequest(string Query, string ExplanationLanguage);

public sealed record WordLookupResult(
    string Headword,
    IReadOnlyList<WordPronunciation> Pronunciations,
    IReadOnlyList<WordSense> Senses,
    IReadOnlyList<WordExample> Examples,
    IReadOnlyList<string> Collocations,
    WordLookupSource Source);

public sealed record WordPronunciation(string Region, string Phonetic);

public sealed record WordSense(
    string PartOfSpeech,
    string Definition,
    string EnglishDefinition);

public sealed record WordExample(string Sentence, string Translation);

public sealed record WordLookupSource(
    string ProviderId,
    string DisplayName,
    WordLookupSourceKind Kind);

public enum WordLookupSourceKind
{
    AiGenerated,
    Dictionary
}

public sealed record WordLookupProviderSettings(
    string ApiBaseUrl,
    string ApiKey,
    string ModelName,
    string ExplanationLanguage);

public sealed class WordLookupNotFoundException : Exception
{
    public WordLookupNotFoundException()
        : base("No lookup result was found.")
    {
    }
}

public sealed class WordLookupFormatException : Exception
{
    public WordLookupFormatException(string message)
        : base(message)
    {
    }

    public WordLookupFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
