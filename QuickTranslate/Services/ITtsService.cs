using System.Threading;
using System.Threading.Tasks;

namespace QuickTranslate.Services;

/// <summary>
/// Text-to-speech service: synthesize + play with cancellation.
/// </summary>
public interface ITtsService : IAsyncDisposable
{
    /// <summary>True while synthesizing or playing.</summary>
    bool IsBusy { get; }

    /// <summary>Raised on synthesize/play start-end, cancel, and fail. Never carries text.</summary>
    event Action? StateChanged;

    Task SpeakAsync(
        string text,
        string? languageHint,
        string? voiceOverride,
        double rate,
        CancellationToken cancellationToken);

    Task StopAsync();
}
