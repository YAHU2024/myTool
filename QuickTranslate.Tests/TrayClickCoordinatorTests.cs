using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TrayClickCoordinatorTests
{
    [Theory]
    [InlineData(false, TrayClickActionKind.ShowLookup)]
    [InlineData(true, TrayClickActionKind.HideLookup)]
    public void SingleClick_UsesOriginalVisibilitySnapshot(
        bool originallyVisible,
        TrayClickActionKind expected)
    {
        using var coordinator = new TrayClickCoordinator();
        var snapshot = coordinator.RecordLeftButtonDown(originallyVisible, new PhysicalPoint(10, 20));

        Assert.Equal(TrayClickActionKind.NoOp, coordinator.RecordDeactivated().Kind);
        var action = coordinator.ConfirmSingleClick(snapshot.Sequence);

        Assert.Equal(expected, action.Kind);
        Assert.Equal(new PhysicalPoint(10, 20), action.Snapshot?.Anchor);
    }

    [Fact]
    public void DoubleClick_CancelsPendingSingleAndOpensSettings()
    {
        using var coordinator = new TrayClickCoordinator();
        var snapshot = coordinator.RecordLeftButtonDown(false, new PhysicalPoint(1, 2));

        Assert.Equal(TrayClickActionKind.OpenSettings, coordinator.RecordDoubleClick().Kind);
        Assert.Equal(TrayClickActionKind.NoOp, coordinator.ConfirmSingleClick(snapshot.Sequence).Kind);
    }

    [Fact]
    public void DeactivationWithoutTrayClick_HidesLookup()
    {
        using var coordinator = new TrayClickCoordinator();
        Assert.Equal(TrayClickActionKind.HideForDeactivation, coordinator.RecordDeactivated().Kind);
    }

    [Fact]
    public void NewerClickInvalidatesOlderTimer()
    {
        using var coordinator = new TrayClickCoordinator();
        var first = coordinator.RecordLeftButtonDown(false, new PhysicalPoint(1, 2));
        var second = coordinator.RecordLeftButtonDown(true, new PhysicalPoint(3, 4));

        Assert.Equal(TrayClickActionKind.NoOp, coordinator.ConfirmSingleClick(first.Sequence).Kind);
        Assert.Equal(TrayClickActionKind.HideLookup, coordinator.ConfirmSingleClick(second.Sequence).Kind);
    }
}
