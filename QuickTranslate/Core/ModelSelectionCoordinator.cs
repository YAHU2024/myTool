using QuickTranslate.Models;

namespace QuickTranslate.Core;

internal enum ModelSelectionIntent
{
    NoOp,
    OpenSettings,
    CancelAndStart,
    StartWith
}

internal sealed record ModelSelectionDecision(
    ModelSelectionIntent Intent,
    ModelProfile? Profile,
    TranslationRequest? Request);

internal sealed class ModelSelectionCoordinator
{
    private Guid? _sessionId;
    private readonly Dictionary<ContentType, (ModelProfile Profile, TranslationRequest Request)> _states = [];
    private ContentType _activeMode;

    public ModelProfile? CurrentProfile => GetCurrentProfile(_activeMode);

    public ModelProfile? GetCurrentProfile(ContentType mode) =>
        _states.TryGetValue(mode, out var state) ? state.Profile : null;

    public void BeginSession(
        Guid sessionId,
        ContentType mode,
        ModelProfile profile,
        TranslationRequest requestTemplate)
    {
        _sessionId = sessionId;
        _activeMode = mode;
        _states[mode] = (profile, requestTemplate);
    }

    public bool IsCurrent(Guid sessionId, ContentType mode) =>
        _sessionId == sessionId && _states.ContainsKey(mode);

    public void RefreshCurrentProfile(ModelProfile profile)
    {
        if (!_states.TryGetValue(_activeMode, out var state))
            return;
        if (state.Profile.Id == profile.Id ||
            (string.Equals(state.Profile.ApiBaseUrl.TrimEnd('/'), profile.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
             string.Equals(state.Profile.ApiKey, profile.ApiKey, StringComparison.Ordinal) &&
             string.Equals(state.Profile.ModelName, profile.ModelName, StringComparison.Ordinal)))
        {
            _states[_activeMode] = (profile, state.Request with
            {
                ApiBaseUrl = profile.ApiBaseUrl,
                ApiKey = profile.ApiKey,
                ModelName = profile.ModelName
            });
        }
    }

    public ModelSelectionDecision Select(
        Guid sessionId,
        ContentType mode,
        ModelProfile? profile,
        bool requestIsRunning)
    {
        if (!IsCurrent(sessionId, mode) || profile is null || !profile.IsComplete)
            return new(ModelSelectionIntent.OpenSettings, profile, null);

        _activeMode = mode;
        var state = _states[mode];
        if (state.Profile.Id == profile.Id ||
            (string.Equals(state.Profile.ApiBaseUrl.TrimEnd('/'), profile.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
             string.Equals(state.Profile.ApiKey, profile.ApiKey, StringComparison.Ordinal) &&
             string.Equals(state.Profile.ModelName, profile.ModelName, StringComparison.Ordinal)))
        {
            return new(ModelSelectionIntent.NoOp, state.Profile, null);
        }

        var request = state.Request with
        {
            ApiBaseUrl = profile.ApiBaseUrl,
            ApiKey = profile.ApiKey,
            ModelName = profile.ModelName
        };
        _states[mode] = (profile, request);
        return new(
            requestIsRunning ? ModelSelectionIntent.CancelAndStart : ModelSelectionIntent.StartWith,
            profile,
            request);
    }

    public bool TryGetRequest(Guid sessionId, ContentType mode, out TranslationRequest? request)
    {
        if (IsCurrent(sessionId, mode))
        {
            request = _states[mode].Request;
            return true;
        }

        request = null;
        return false;
    }

    public bool TryApplyCurrentProfile(
        Guid sessionId,
        ContentType mode,
        TranslationRequest semanticRequest,
        out TranslationRequest? request)
    {
        ArgumentNullException.ThrowIfNull(semanticRequest);
        if (!IsCurrent(sessionId, mode))
        {
            request = null;
            return false;
        }

        _activeMode = mode;
        request = semanticRequest with
        {
            ApiBaseUrl = _states[mode].Profile.ApiBaseUrl,
            ApiKey = _states[mode].Profile.ApiKey,
            ModelName = _states[mode].Profile.ModelName
        };
        _states[mode] = (_states[mode].Profile, request);
        return true;
    }

    public void Reset()
    {
        _sessionId = null;
        _states.Clear();
    }
}
