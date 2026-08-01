using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuickTranslate.Database;

namespace QuickTranslate.Services
{
    /// <summary>
    /// Exports translation history records to Anki-compatible CSV/TSV/TXT files
    /// with spreadsheet formula injection prevention.
    /// </summary>
    public static class HistoryExporter
    {
        /// <summary>
        /// Characters that may trigger formula execution in spreadsheet applications
        /// when placed at the beginning of a cell.
        /// </summary>
        private static readonly HashSet<char> DangerousFirstChars = new()
        {
            '=', '+', '-', '@'
        };

        /// <summary>
        /// Export records to the specified file path, using appropriate escaping
        /// based on file extension (.csv, .tsv, .txt).
        /// </summary>
        public static void Export(IReadOnlyList<TranslationRecord> records, string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var separator = extension == ".tsv" ? "\t" : ",";
            var isText = extension == ".txt";

            var sb = new StringBuilder();

            // Write header (Anki format)
            sb.AppendLine($"原文{separator}译文{separator}源语言{separator}目标语言{separator}模型{separator}时间");

            foreach (var record in records)
            {
                var source = EscapeField(record.SourceText, separator, isText);
                var translation = EscapeField(record.Translation, separator, isText);
                var sourceLang = EscapeField(record.SourceLanguage, separator, isText);
                var targetLang = EscapeField(record.TargetLanguage, separator, isText);
                var model = EscapeField(record.ModelName, separator, isText);
                var time = record.TranslatedAt.ToString("yyyy-MM-dd HH:mm:ss");

                sb.AppendLine($"{source}{separator}{translation}{separator}{sourceLang}{separator}{targetLang}{separator}{model}{separator}{time}");
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Escape a single field value for CSV/TSV output.
        /// Applies formula injection neutralization for spreadsheet formats.
        /// </summary>
        /// <param name="field">The raw field value.</param>
        /// <param name="separator">The field separator character or string.</param>
        /// <param name="isPlainText">
        /// When true (e.g., .txt export), formula injection neutralization is skipped.
        /// </param>
        /// <returns>The escaped field value safe for the target format.</returns>
        public static string EscapeField(string field, string separator, bool isPlainText = false)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            // Neutralize formula injection for spreadsheet formats
            if (!isPlainText && IsFormulaInjectionCandidate(field))
            {
                field = NeutralizeFormulaInjection(field);
            }

            // Quote if field contains separator, newline, or double-quote
            if (field.Contains(separator) || field.Contains('\n') || field.Contains('\r') || field.Contains('"'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }

        /// <summary>
        /// Determines whether a field value could be interpreted as a formula
        /// by spreadsheet applications (Excel, LibreOffice Calc, etc.).
        /// </summary>
        /// <remarks>
        /// Checks if the field, after trimming leading whitespace, starts with
        /// a dangerous character: '=', '+', '-', or '@'.
        /// </remarks>
        public static bool IsFormulaInjectionCandidate(string field)
        {
            if (string.IsNullOrEmpty(field))
                return false;

            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];

                // Skip leading whitespace and tab characters
                if (c == ' ' || c == '\t')
                    continue;

                return DangerousFirstChars.Contains(c);
            }

            return false;
        }

        /// <summary>
        /// Neutralize a formula injection candidate by prefixing with a tab character.
        /// The tab is prepended before any leading whitespace so that the spreadsheet
        /// treats the cell as text rather than a formula.
        /// </summary>
        /// <remarks>
        /// A leading tab ('\t') is the recommended neutralization method because:
        /// - It prevents Excel/LibreOffice from interpreting the cell as a formula.
        /// - It is invisible in most spreadsheet renderings.
        /// - It does not break CSV/TSV parsers.
        /// - It preserves the original field content for Anki imports.
        /// </remarks>
        public static string NeutralizeFormulaInjection(string field)
        {
            return "\t" + field;
        }
    }
}
