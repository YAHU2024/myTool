using QuickTranslate.Models;

namespace QuickTranslate.Services;

public interface IWordLookupEnrichmentService
{
    Task<WordLookupResult> EnrichAsync(
        WordLookupRequest request,
        WordLookupResult localResult,
        CancellationToken cancellationToken);
}
