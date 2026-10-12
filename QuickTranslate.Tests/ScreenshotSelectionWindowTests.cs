using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

[CollectionDefinition("Screenshot selection input", DisableParallelization = true)]
public sealed class ScreenshotSelectionInputCollection;

[Collection("Screenshot selection input")]
public sealed class ScreenshotSelectionWindowTests
{
    [SkippableFact]
    public void RightClickBeforeDragging_CancelsWithoutCompletingSelection() =>
        VerifyCancellation(startDragging: false, useEscape: false);

    [SkippableFact]
    public void RightClickDuringDragging_ReleasesCaptureAndDoesNotCompleteOnLeftButtonUp() =>
        VerifyCancellation(startDragging: true, useEscape: false);

    [SkippableFact]
    public void EscapeDuringDragging_StillReleasesCaptureAndCancels() =>
        VerifyCancellation(startDragging: true, useEscape: true);

    private static void VerifyCancellation(bool startDragging, bool useEscape)
    {
        Skip.If(Environment.GetEnvironmentVariable("CI") == "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "WPF input tests require an interactive Windows desktop.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ScreenshotSelectionWindow? window = null;
            try
            {
                Assert.True(Win32Api.GetCursorPos(out var cursor));
                window = new ScreenshotSelectionWindow(new ScreenshotRegion(cursor.X - 50, cursor.Y - 50, 100, 100));
                var cancelledCount = 0;
                var completedCount = 0;
                window.Cancelled += () => cancelledCount++;
                window.SelectionCompleted += _ => completedCount++;
                window.ShowSelection();
                _ = Assert.IsType<Canvas>(window.FindName("SelectionCanvas"));

                if (startDragging)
                {
                    var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent
                    };
                    window.RaiseEvent(down);
                    Assert.True(down.Handled);
                    Assert.True(window.IsMouseCaptured);
                }

                if (useEscape)
                {
                    var source = PresentationSource.FromVisual(window);
                    Assert.NotNull(source);
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    window.RaiseEvent(escape);
                    Assert.True(escape.Handled);
                }
                else
                {
                    var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    {
                        RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent
                    };
                    window.RaiseEvent(rightClick);
                    Assert.True(rightClick.Handled);
                }

                Assert.False(window.IsVisible);
                Assert.False(window.IsMouseCaptured);
                Assert.Equal(1, cancelledCount);
                Assert.Equal(0, completedCount);

                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                });
                window.CancelSelection();
                Assert.Equal(1, cancelledCount);
                Assert.Equal(0, completedCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }
}
