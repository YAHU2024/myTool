using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class UiaCircuitBreakerTests
{
    [Fact]
    public void FailureThreshold_OnlyDisablesTargetCircuit()
    {
        var selection = new UiaCircuitBreaker("selection", maxFailures: 2);
        var focus = new UiaCircuitBreaker("focus", maxFailures: 2);

        selection.RecordFailure("COMException");
        selection.RecordFailure("Timeout");

        Assert.True(selection.IsDisabled);
        Assert.False(focus.IsDisabled);
        Assert.Equal(0, focus.FailureCount);
    }

    [Fact]
    public void Success_ResetsConsecutiveFailureCount()
    {
        var circuit = new UiaCircuitBreaker("selection", maxFailures: 3);

        circuit.RecordFailure("Timeout");
        circuit.RecordSuccess();
        circuit.RecordFailure("Timeout");

        Assert.False(circuit.IsDisabled);
        Assert.Equal(1, circuit.FailureCount);
    }
}
