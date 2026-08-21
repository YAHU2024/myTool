namespace QuickTranslate.Models;

public enum ThinkingModePreference
{
    FollowProviderDefault,
    Enabled,
    Disabled
}

internal static class ThinkingModePreferences
{
    internal static ThinkingModePreference Normalize(ThinkingModePreference preference) =>
        Enum.IsDefined(preference)
            ? preference
            : ThinkingModePreference.FollowProviderDefault;
}
