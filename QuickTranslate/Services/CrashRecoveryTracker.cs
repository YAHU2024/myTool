using System.Text.Json;
using System.IO;

namespace QuickTranslate.Services;

public enum RecoveryPromptState
{
    Pending,
    Shown,
    Dismissed,
    FeedbackStarted
}

public sealed record RecoveryEvent(
    string RunId,
    DateTimeOffset StartedAt,
    string AppVersion,
    string Architecture,
    string? ErrorType,
    string? ErrorCode,
    RecoveryPromptState PromptState);

public sealed class CrashRecoveryTracker
{
    private readonly string _directory;
    private readonly string _currentPath;
    private readonly string _pendingPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private RecoveryEvent? _pending;
    private bool _started;

    public CrashRecoveryTracker(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickTranslate");
        _currentPath = Path.Combine(_directory, "current-run.json");
        _pendingPath = Path.Combine(_directory, "pending-recovery.json");
    }

    public RecoveryEvent? StartRun(string appVersion, string architecture, DateTimeOffset now)
    {
        // Production calls this once. Keeping it idempotent prevents callers or
        // tests from treating the current process's own running state as a crash.
        if (_started)
            return null;

        _started = true;
        TryCreateDirectory();
        var previous = Read<RunState>(_currentPath);
        _pending = Read<RecoveryEvent>(_pendingPath);
        var recoveryToShow = _pending?.PromptState == RecoveryPromptState.Pending
            ? _pending
            : null;

        // Keep an existing pending event. Once it has been shown or handled, a
        // later unclean run may replace it because only one event is retained.
        if (previous?.Status == "running" &&
            (_pending is null || _pending.PromptState != RecoveryPromptState.Pending))
        {
            _pending = new RecoveryEvent(
                previous.RunId,
                previous.StartedAt,
                previous.AppVersion,
                previous.Architecture,
                previous.ErrorType,
                previous.ErrorCode,
                RecoveryPromptState.Pending);
            WriteAtomic(_pendingPath, _pending);
            recoveryToShow = _pending;
        }

        var current = new RunState(
            Guid.NewGuid().ToString("N"), now, appVersion, architecture, "running", null, null);
        WriteAtomic(_currentPath, current);
        return recoveryToShow;
    }

    public void MarkClean()
    {
        var current = Read<RunState>(_currentPath);
        if (current is null)
            return;
        WriteAtomic(_currentPath, current with { Status = "clean" });
    }

    public RecoveryEvent? MarkShown() => UpdatePending(RecoveryPromptState.Shown);
    public RecoveryEvent? MarkDismissed() => UpdatePending(RecoveryPromptState.Dismissed);
    public RecoveryEvent? MarkFeedbackStarted() => UpdatePending(RecoveryPromptState.FeedbackStarted);

    public RecoveryEvent? PendingEvent => _pending is { PromptState: RecoveryPromptState.Pending } ? _pending : null;

    private RecoveryEvent? UpdatePending(RecoveryPromptState state)
    {
        if (_pending is null)
            return null;
        _pending = _pending with { PromptState = state };
        WriteAtomic(_pendingPath, _pending);
        return _pending;
    }

    private T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
                return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _jsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private void WriteAtomic<T>(string path, T value)
    {
        try
        {
            TryCreateDirectory();
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, _jsonOptions));
            File.Move(temp, path, true);
        }
        catch
        {
            // Recovery state must never prevent the application from starting or exiting.
        }
    }

    private void TryCreateDirectory()
    {
        try { Directory.CreateDirectory(_directory); }
        catch { }
    }

    private sealed record RunState(
        string RunId,
        DateTimeOffset StartedAt,
        string AppVersion,
        string Architecture,
        string Status,
        string? ErrorType,
        string? ErrorCode);
}
