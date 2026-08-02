using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class CompositeWordLookupService : IWordLookupService
{
    private readonly IWordLookupService _primary;
    private readonly IWordLookupService _fallback;

    public CompositeWordLookupService(
        IWordLookupService primary,
        IWordLookupService fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<WordLookupResult> LookupAsync(
        WordLookupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _primary.LookupAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WordLookupNotFoundException)
        {
            return await _fallback.LookupAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
