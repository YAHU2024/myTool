using QuickTranslate.Models;

namespace QuickTranslate.Services;

public interface IWordLookupService
{
    Task<WordLookupResult> LookupAsync(
        WordLookupRequest request,
        CancellationToken cancellationToken);
}
