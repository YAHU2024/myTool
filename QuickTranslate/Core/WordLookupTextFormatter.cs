using System.Text;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

public static class WordLookupTextFormatter
{
    public static string Format(WordLookupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder(result.Headword);
        if (result.Pronunciations.Count > 0)
        {
            text.AppendLine();
            text.AppendJoin("  ", result.Pronunciations.Select(item =>
                string.IsNullOrWhiteSpace(item.Region)
                    ? item.Phonetic
                    : $"{item.Region} {item.Phonetic}"));
        }

        foreach (var sense in result.Senses)
        {
            text.AppendLine().AppendLine();
            if (!string.IsNullOrWhiteSpace(sense.PartOfSpeech))
                text.Append(sense.PartOfSpeech).Append(' ');
            if (!string.IsNullOrWhiteSpace(sense.Definition))
                text.Append(sense.Definition);
            if (!string.IsNullOrWhiteSpace(sense.EnglishDefinition))
            {
                if (!string.IsNullOrWhiteSpace(sense.Definition))
                    text.AppendLine();
                text.Append(sense.EnglishDefinition);
            }
        }

        foreach (var example in result.Examples)
        {
            text.AppendLine().AppendLine().Append(example.Sentence);
            if (!string.IsNullOrWhiteSpace(example.Translation))
                text.AppendLine().Append(example.Translation);
        }

        if (result.Collocations.Count > 0)
            text.AppendLine().AppendLine().Append("常用搭配：").AppendJoin("；", result.Collocations);

        text.AppendLine().AppendLine().Append(result.Source.DisplayName);
        return text.ToString();
    }
}
