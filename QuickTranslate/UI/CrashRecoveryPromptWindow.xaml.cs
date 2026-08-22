using System.Windows;

namespace QuickTranslate.UI;

public partial class CrashRecoveryPromptWindow : Window
{
    public event EventHandler? FeedbackRequested;
    public event EventHandler? Dismissed;
    public event EventHandler? DoNotPromptAgainRequested;

    public CrashRecoveryPromptWindow()
    {
        InitializeComponent();
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e) => FeedbackRequested?.Invoke(this, EventArgs.Empty);
    private void DismissButton_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
    private void DoNotPromptAgainButton_Click(object sender, RoutedEventArgs e) => DoNotPromptAgainRequested?.Invoke(this, EventArgs.Empty);
}
