using System.Text.Json.Serialization;

namespace QuickTranslate.Helpers;

/// <summary>
/// A privacy-safe structured application log record.
/// Context values must contain diagnostics only; user content and credentials are prohibited.
/// </summary>
public sealed record LogEvent(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,
    string EventName,
    IReadOnlyDictionary<string, object?> Context)
{
    [JsonIgnore]
    public string Message => EventName;
}
