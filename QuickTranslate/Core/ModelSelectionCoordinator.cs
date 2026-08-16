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
    private ContentType _mode;
    private ModelProfile? _currentProfile;
    private TranslationRequest? _requestTemplate;

    public ModelProfile? CurrentProfile => _currentProfile;

    public void BeginSession(
        Guid sessionId,
        ContentType mode,
        ModelProfile profile,
        TranslationRequest requestTemplate)
    {
        _sessionId = sessionId;
        _mode = mode;
        _currentProfile = profile;
        _requestTemplate = requestTemplate;
    }

    public bool IsCurrent(Guid sessionId, ContentType mode) =>
        _sessionId == sessionId && _mode == mode && _currentProfile is not null && _requestTemplate is not null;

    public void RefreshCurrentProfile(ModelProfile profile)
    {
        if (_currentProfile is null || _requestTemplate is null)
            return;
        if (_currentProfile.Id == profile.Id ||
            (string.Equals(_currentProfile.ApiBaseUrl.TrimEnd('/'), profile.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
             string.Equals(_currentProfile.ApiKey, profile.ApiKey, StringComparison.Ordinal) &&
             string.Equals(_currentProfile.ModelName, profile.ModelName, StringComparison.Ordinal)))
        {
            _currentProfile = profile;
            _requestTemplate = _requestTemplate with
            {
                ApiBaseUrl = profile.ApiBaseUrl,
                ApiKey = profile.ApiKey,
                ModelName = profile.ModelName
            };
        }
    }

    public ModelSelectionDecision Select(
        Guid sessionId,
        ContentType mode,
        ModelProfile? profile,
        bool requestIsRunning)
    {
        if (!IsCurrent(sessionId, mode) || mode != ContentType.Translation || profile is null || !profile.IsComplete)
            return new(ModelSelectionIntent.OpenSettings, profile, null);

        if (_currentProfile!.Id == profile.Id ||
            (string.Equals(_currentProfile.ApiBaseUrl.TrimEnd('/'), profile.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
             string.Equals(_currentProfile.ApiKey, profile.ApiKey, StringComparison.Ordinal) &&
             string.Equals(_currentProfile.ModelName, profile.ModelName, StringComparison.Ordinal)))
        {
            return new(ModelSelectionIntent.NoOp, _currentProfile, null);
        }

        _currentProfile = profile;
        var request = _requestTemplate! with
        {
            ApiBaseUrl = profile.ApiBaseUrl,
            ApiKey = profile.ApiKey,
            ModelName = profile.ModelName
        };
        _requestTemplate = request;
        return new(
            requestIsRunning ? ModelSelectionIntent.CancelAndStart : ModelSelectionIntent.StartWith,
            profile,
            request);
    }

    public bool TryGetRequest(Guid sessionId, ContentType mode, out TranslationRequest? request)
    {
        if (IsCurrent(sessionId, mode))
        {
            request = _requestTemplate;
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

        request = semanticRequest with
        {
            ApiBaseUrl = _currentProfile!.ApiBaseUrl,
            ApiKey = _currentProfile.ApiKey,
            ModelName = _currentProfile.ModelName
        };
        _requestTemplate = request;
        return true;
    }

    public void Reset()
    {
        _sessionId = null;
        _currentProfile = null;
        _requestTemplate = null;
    }
}
