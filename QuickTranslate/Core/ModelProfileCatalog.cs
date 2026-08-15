using QuickTranslate.Models;

namespace QuickTranslate.Core;

internal sealed record ModelProfile(
    string Id,
    string Alias,
    string ModelName,
    string ProviderName,
    string ApiBaseUrl,
    string ApiKey,
    bool IsTemporary = false)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ModelName);

    public string DisplayName => !string.IsNullOrWhiteSpace(Alias)
        ? Alias
        : $"{ModelName} · {ProviderName}";

    public string SelectorDisplayName => !string.IsNullOrWhiteSpace(Alias)
        ? Alias
        : ModelProfileCatalog.CompactModelName(ModelName);

    public string MenuDetail => $"{ModelName} · {ProviderName}";
}

internal static class ModelProfileCatalog
{
    private const int MaxAliasLength = 32;

    public static IReadOnlyList<ModelProfile> Build(
        IEnumerable<SavedConfig>? savedConfigs,
        TranslationRequest? currentRequest = null,
        string? currentProfileId = null)
    {
        var profiles = (savedConfigs ?? [])
            .Where(config => config is not null)
            .Select(Create)
            .GroupBy(profile => profile.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (currentRequest is null)
            return profiles;

        var matching = profiles.FirstOrDefault(profile => Matches(profile, currentRequest));
        if (matching is not null)
            return profiles;

        profiles.Insert(0, new ModelProfile(
            string.IsNullOrWhiteSpace(currentProfileId)
                ? $"current:{Guid.NewGuid():N}"
                : currentProfileId,
            string.Empty,
            currentRequest.ModelName,
            ResolveProviderName(currentRequest.ApiBaseUrl),
            currentRequest.ApiBaseUrl,
            currentRequest.ApiKey,
            IsTemporary: true));
        return profiles;
    }

    public static ModelProfile Create(SavedConfig config) => new(
        EnsureId(config),
        ResolveLegacyAlias(config),
        config.ModelName?.Trim() ?? string.Empty,
        ResolveProviderName(config.ApiBaseUrl),
        config.ApiBaseUrl?.Trim() ?? string.Empty,
        config.ApiKey ?? string.Empty);

    public static ModelProfile CreateCurrent(TranslationRequest request, string? profileId = null) => new(
        string.IsNullOrWhiteSpace(profileId) ? $"current:{Guid.NewGuid():N}" : profileId,
        string.Empty,
        request.ModelName,
        ResolveProviderName(request.ApiBaseUrl),
        request.ApiBaseUrl,
        request.ApiKey,
        IsTemporary: true);

    public static string NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return string.Empty;

        var sanitized = new string(alias
            .Where(character => character is not '\r' and not '\n' && !char.IsControl(character))
            .ToArray())
            .Trim();
        return sanitized.Length <= MaxAliasLength
            ? sanitized
            : sanitized[..MaxAliasLength];
    }

    public static string CompactModelName(string? modelName)
    {
        var normalized = modelName?.Trim() ?? string.Empty;
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 && separatorIndex < normalized.Length - 1
            ? normalized[(separatorIndex + 1)..]
            : normalized;
    }

    public static string ResolveLegacyAlias(SavedConfig config)
    {
        var alias = NormalizeAlias(config.Alias);
        if (!string.IsNullOrWhiteSpace(alias))
            return alias;

        var legacy = NormalizeAlias(config.DisplayName);
        return string.Equals(legacy, config.ModelName?.Trim(), StringComparison.Ordinal)
            ? string.Empty
            : legacy;
    }

    public static bool Matches(ModelProfile profile, TranslationRequest request) =>
        string.Equals(NormalizeBaseUrl(profile.ApiBaseUrl), NormalizeBaseUrl(request.ApiBaseUrl), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(profile.ApiKey, request.ApiKey, StringComparison.Ordinal) &&
        string.Equals(profile.ModelName, request.ModelName, StringComparison.Ordinal);

    public static string ResolveProviderName(string? apiBaseUrl)
    {
        var normalized = NormalizeBaseUrl(apiBaseUrl);
        var preset = ProviderPresetCatalog.All.FirstOrDefault(candidate =>
            string.Equals(NormalizeBaseUrl(candidate.ApiBaseUrl), normalized, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
            return preset.DisplayName;

        return Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "未知供应商";
    }

    private static string EnsureId(SavedConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Id))
            return config.Id;
        config.Id = $"provider:{Guid.NewGuid():N}";
        return config.Id;
    }

    private static string NormalizeBaseUrl(string? apiBaseUrl) =>
        (apiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
}
