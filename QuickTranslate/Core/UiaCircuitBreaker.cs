using System.Threading;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core;

internal sealed class UiaCircuitBreaker
{
    private readonly string _capability;
    private readonly int _maxFailures;
    private int _failureCount;
    private int _disabled;

    internal UiaCircuitBreaker(string capability, int maxFailures = 3)
    {
        _capability = capability;
        _maxFailures = maxFailures;
    }

    internal bool IsDisabled => Volatile.Read(ref _disabled) != 0;

    internal int FailureCount => Volatile.Read(ref _failureCount);

    internal void RecordSuccess() => Interlocked.Exchange(ref _failureCount, 0);

    internal void RecordFailure(string errorType)
    {
        var failures = Interlocked.Increment(ref _failureCount);
        if (failures >= _maxFailures)
        {
            Interlocked.Exchange(ref _disabled, 1);
            Logger.Error("SelectionLocator", "uia.circuit_open", new
            {
                capability = _capability,
                failures,
                error_type = errorType
            });
            return;
        }

        Logger.Warn("SelectionLocator", "uia.sta_failed", new
        {
            capability = _capability,
            failures,
            max_failures = _maxFailures,
            error_type = errorType
        });
    }
}
