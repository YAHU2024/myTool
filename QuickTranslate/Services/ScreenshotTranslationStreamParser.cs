using System.Text;
using System.Text.Json;

namespace QuickTranslate.Services;

/// <summary>
/// 解析截图翻译的结构化内容流。
///
/// Provider 可能把一个 JSON 对象拆成多个 content delta，也可能返回完整
/// <c>{"units":[...]}</c> 或逐单元 JSON 对象。解析器只在取得完整对象后发布，
/// 因此 UI 永远不会看到半截 JSON 或按顺序猜测出的坐标。
/// </summary>
public sealed class ScreenshotTranslationStreamParser
{
    private const int MaxBufferedCharacters = 256_000;
    private readonly HashSet<string> _expectedIds;
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly List<TranslatedTextUnit> _translated = new();
    private readonly StringBuilder _buffer = new();
    private bool _invalid;
    private string _reason = "invalid_stream";

    public ScreenshotTranslationStreamParser(IEnumerable<string> expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        _expectedIds = new HashSet<string>(expectedIds, StringComparer.Ordinal);
        if (_expectedIds.Count == 0)
            throw new ArgumentException("截图流式翻译必须至少包含一个预期 ID。", nameof(expectedIds));
    }

    public bool IsInvalid => _invalid;

    public string FailureReason => _reason;

    public IReadOnlyList<TranslatedTextUnit> Append(string? content)
    {
        if (_invalid || string.IsNullOrEmpty(content))
            return Array.Empty<TranslatedTextUnit>();

        _buffer.Append(content);
        if (_buffer.Length > MaxBufferedCharacters)
        {
            Reject("stream_too_large");
            return Array.Empty<TranslatedTextUnit>();
        }
        var emitted = new List<TranslatedTextUnit>();
        while (!_invalid && TryTakeJsonValue(out var json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                ParseRoot(document.RootElement, emitted);
            }
            catch (JsonException)
            {
                Reject("invalid_json");
            }
        }

        return emitted;
    }

    public ScreenshotTranslationMappingResult Complete(
        IReadOnlyList<ScreenshotTranslationUnit> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (_invalid)
            return new(false, _reason, _translated.ToArray(), expected.ToArray());
        if (_buffer.ToString().Trim().Length > 0)
            return new(false, "incomplete_json", _translated.ToArray(), expected.ToArray());

        var mapped = ScreenshotTranslationMapper.Map(expected, _translated);
        return mapped.Accepted
            ? mapped
            : mapped with { Reason = mapped.Reason == "missing_id" ? "missing_id" : mapped.Reason };
    }

    private void ParseRoot(JsonElement root, ICollection<TranslatedTextUnit> emitted)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Reject("invalid_unit");
            return;
        }

        if (root.TryGetProperty("units", out var units))
        {
            if (units.ValueKind != JsonValueKind.Array)
            {
                Reject("invalid_units");
                return;
            }

            foreach (var unit in units.EnumerateArray())
                ParseUnit(unit, emitted);
            return;
        }

        ParseUnit(root, emitted);
    }

    private void ParseUnit(JsonElement element, ICollection<TranslatedTextUnit> emitted)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("translation", out var translation) ||
            translation.ValueKind != JsonValueKind.String)
        {
            Reject("invalid_unit");
            return;
        }

        var unitId = id.GetString() ?? string.Empty;
        var translatedText = translation.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(unitId))
        {
            Reject("invalid_id");
            return;
        }
        if (!_expectedIds.Contains(unitId))
        {
            Reject("unexpected_id");
            return;
        }
        if (!_seenIds.Add(unitId))
        {
            Reject("duplicate_id");
            return;
        }
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            Reject("empty_translation");
            return;
        }

        var result = new TranslatedTextUnit(unitId, translatedText.Trim());
        _translated.Add(result);
        emitted.Add(result);
    }

    private bool TryTakeJsonValue(out string json)
    {
        json = string.Empty;
        var value = _buffer.ToString();
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start]))
            start++;
        if (start > 0)
            _buffer.Remove(0, start);
        if (_buffer.Length == 0)
            return false;

        var first = _buffer[0];
        if (first is not ('{' or '['))
        {
            Reject("unexpected_stream_content");
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < _buffer.Length; index++)
        {
            var ch = _buffer[index];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }
            if (ch is '{' or '[')
                depth++;
            else if (ch is '}' or ']')
            {
                depth--;
                if (depth < 0)
                {
                    Reject("invalid_json");
                    return false;
                }
                if (depth == 0)
                {
                    json = _buffer.ToString(0, index + 1);
                    _buffer.Remove(0, index + 1);
                    return true;
                }
            }
        }

        return false;
    }

    private void Reject(string reason)
    {
        _invalid = true;
        _reason = string.IsNullOrWhiteSpace(reason) ? "invalid_stream" : reason;
    }
}
