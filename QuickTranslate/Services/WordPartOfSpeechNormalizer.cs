namespace QuickTranslate.Services;

public static class WordPartOfSpeechNormalizer
{
    public static string ToCanonical(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return string.Empty;

        var key = text.ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal).Trim();
        return key switch
        {
            "n" or "noun" or "名词" => "noun",
            "v" or "verb" or "vt" or "vi" or "vtr" or "vintr" or "动词" => "verb",
            "a" or "adj" or "adjective" or "s" or "形容词" => "adjective",
            "adv" or "adverb" or "ad" or "r" or "副词" => "adverb",
            "pron" or "pronoun" or "代词" => "pronoun",
            "prep" or "preposition" or "介词" => "preposition",
            "conj" or "conjunction" or "连词" => "conjunction",
            "interj" or "int" or "interjection" or "感叹词" => "interjection",
            "det" or "determiner" or "限定词" => "determiner",
            "art" or "article" or "冠词" => "article",
            "num" or "numeral" or "number" or "数词" => "numeral",
            "aux" or "auxiliary" or "auxiliary verb" or "助动词" => "auxiliary",
            "modal" or "modal verb" or "情态动词" => "modal",
            "phrasal verb" or "短语动词" => "phrasal verb",
            "idiom" or "习语" => "idiom",
            "phrase" or "短语" => "phrase",
            "abbr" or "abbreviation" or "缩写" => "abbreviation",
            "other" or "其他" => "other",
            _ when text.Any(character => character is >= '\u3400' and <= '\u9fff') => text,
            _ => "other"
        };
    }

    public static string ToDisplayLabel(string value) => ToCanonical(value) switch
    {
        "noun" => "名词",
        "verb" => "动词",
        "adjective" => "形容词",
        "adverb" => "副词",
        "pronoun" => "代词",
        "preposition" => "介词",
        "conjunction" => "连词",
        "interjection" => "感叹词",
        "determiner" => "限定词",
        "article" => "冠词",
        "numeral" => "数词",
        "auxiliary" => "助动词",
        "modal" => "情态动词",
        "phrasal verb" => "短语动词",
        "idiom" => "习语",
        "phrase" => "短语",
        "abbreviation" => "缩写",
        "other" => "其他",
        var chinese => chinese
    };
}
