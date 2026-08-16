using System.Windows.Media;

namespace QuickTranslate.UI;

/// <summary>
/// Shared floating-window footer status kinds and muted palette.
/// </summary>
public enum FloatingStatusKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Pure helpers for the reusable bottom status strip.
/// </summary>
public static class FloatingStatusMessage
{
    public static readonly TimeSpan DefaultTransientDuration = TimeSpan.FromSeconds(4.5);
    public static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(3.5);

    public const string TransientToken = "transient";

    public static (Color Indicator, Color Foreground) GetAccentColors(FloatingStatusKind kind) =>
        kind switch
        {
            FloatingStatusKind.Success =>
                (Color.FromRgb(0x66, 0xC4, 0x85), Color.FromRgb(0x9F, 0xD4, 0xA8)),
            FloatingStatusKind.Warning =>
                (Color.FromRgb(0xDC, 0xAE, 0x54), Color.FromRgb(0xE6, 0xC0, 0x7B)),
            FloatingStatusKind.Error =>
                (Color.FromRgb(0xD8, 0x70, 0x70), Color.FromRgb(0xE8, 0xA0, 0xA0)),
            _ =>
                (Color.FromRgb(0x67, 0x96, 0xE8), Color.FromRgb(0xB7, 0xC5, 0xFF)),
        };

    public static TimeSpan ResolveDuration(FloatingStatusKind kind, TimeSpan? requested) =>
        requested ?? kind switch
        {
            FloatingStatusKind.Success => SuccessDuration,
            FloatingStatusKind.Warning => WarningDuration,
            FloatingStatusKind.Error => DefaultTransientDuration,
            _ => DefaultTransientDuration
        };
}
