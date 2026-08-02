using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickTranslate.UI;

internal static class TransientButtonFeedback
{
    public static void ShowCopySuccess(Button button, object originalContent, string successContent = "\uE73E")
    {
        var originalFontFamily = button.FontFamily;
        button.FontFamily = new FontFamily("Segoe MDL2 Assets");
        button.Content = successContent;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            button.Content = originalContent;
            button.FontFamily = originalFontFamily;
            timer.Stop();
        };
        timer.Start();
    }
}
