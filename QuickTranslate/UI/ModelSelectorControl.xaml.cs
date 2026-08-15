using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickTranslate.Core;

namespace QuickTranslate.UI;

public partial class ModelSelectorControl : UserControl
{
    private readonly DispatcherTimer _hoverDelay;
    private IReadOnlyList<ModelProfile> _profiles = [];
    private ModelProfile? _currentProfile;
    private Storyboard? _scrollStoryboard;

    internal event Action<string>? ProfileSelected;
    internal event Action? SettingsRequested;
    internal event Action? MenuOpened;
    internal event Action? MenuClosed;

    public ModelSelectorControl()
    {
        InitializeComponent();
        _hoverDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverDelay.Tick += (_, _) =>
        {
            _hoverDelay.Stop();
            BeginNameScroll();
        };
        SelectorButton.MouseEnter += (_, _) => ScheduleNameScroll();
        SelectorButton.MouseLeave += (_, _) => ResetNameScroll();
        SelectorButton.GotKeyboardFocus += (_, _) => ScheduleNameScroll();
        SelectorButton.LostKeyboardFocus += (_, _) => ResetNameScroll();
        NameViewport.SizeChanged += (_, _) => ResetNameScroll();
    }

    internal void SetProfiles(
        IReadOnlyList<ModelProfile> profiles,
        ModelProfile? currentProfile,
        bool enabled)
    {
        _profiles = profiles;
        _currentProfile = currentProfile;
        if (!enabled)
            SelectorPopup.IsOpen = false;
        IsEnabled = enabled;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ModelNameText.Text = currentProfile?.SelectorDisplayName ?? "选择模型";
        SelectorButton.ToolTip = currentProfile is null
            ? "选择当前会话模型"
            : BuildToolTip(currentProfile);
        ResetNameScroll();
        if (SelectorPopup.IsOpen)
            PopulateMenu();
    }

    private void SelectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectorPopup.IsOpen)
        {
            SelectorPopup.IsOpen = false;
            return;
        }

        PopulateMenu();
        SelectorPopup.HorizontalOffset = 0;
        PopupAnchor.Margin = new Thickness(
            0,
            -1,
            Math.Max(0, (SelectorButton.ActualWidth - PopupAnchor.Width) / 2),
            0);
        SelectorPopup.IsOpen = true;
    }

    private void PopulateMenu()
    {
        var entries = _profiles
            .Select(profile => new ModelMenuEntry(
                profile,
                string.Equals(_currentProfile?.Id, profile.Id, StringComparison.Ordinal),
                BuildToolTip(profile)))
            .ToList();
        ProfileList.ItemsSource = entries;
        ProfileList.SelectedItem = entries.FirstOrDefault(entry => entry.IsCurrent)
            ?? entries.FirstOrDefault(entry => entry.IsComplete);
    }

    private void SelectorPopup_Opened(object sender, EventArgs e)
    {
        MenuOpened?.Invoke();
        Dispatcher.BeginInvoke(() =>
        {
            if (ProfileList.SelectedItem is ModelMenuEntry { IsComplete: true } selectedEntry)
            {
                ProfileList.ScrollIntoView(ProfileList.SelectedItem);
                ProfileList.UpdateLayout();
                if (ProfileList.ItemContainerGenerator.ContainerFromItem(selectedEntry) is ListBoxItem item)
                    item.Focus();
                else
                    ProfileList.Focus();
            }
            else
            {
                ManageModelsButton.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void SelectorPopup_Closed(object sender, EventArgs e)
    {
        MenuClosed?.Invoke();
        SelectorButton.Focus();
    }

    private void ProfileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ProfileList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not ModelMenuEntry entry || !entry.IsComplete)
            return;

        e.Handled = true;
        SelectEntry(entry);
    }

    private void ProfileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SelectorPopup.IsOpen = false;
            return;
        }

        if (e.Key is not (Key.Enter or Key.Space) || ProfileList.SelectedItem is not ModelMenuEntry entry)
            return;

        e.Handled = true;
        SelectEntry(entry);
    }

    private void SelectEntry(ModelMenuEntry entry)
    {
        if (!entry.IsComplete)
            return;

        SelectorPopup.IsOpen = false;
        if (!entry.IsCurrent)
            ProfileSelected?.Invoke(entry.Id);
    }

    private void ManageModelsButton_Click(object sender, RoutedEventArgs e)
    {
        SelectorPopup.IsOpen = false;
        SettingsRequested?.Invoke();
    }

    private void ScheduleNameScroll()
    {
        if (!IsNameClipped())
            return;
        _hoverDelay.Stop();
        _hoverDelay.Start();
    }

    private void BeginNameScroll()
    {
        if (!IsNameClipped())
            return;

        ModelNameText.TextTrimming = TextTrimming.None;
        var distance = Math.Max(0, ModelNameText.ActualWidth - NameViewport.ActualWidth);
        if (distance <= 0.5)
            return;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.5))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.3))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.8))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(6.6))));
        _scrollStoryboard = new Storyboard();
        _scrollStoryboard.Children.Add(animation);
        Storyboard.SetTarget(animation, ModelNameTransform);
        Storyboard.SetTargetProperty(animation, new PropertyPath("X"));
        _scrollStoryboard.Begin(this, true);
    }

    private void ResetNameScroll()
    {
        _hoverDelay.Stop();
        _scrollStoryboard?.Remove(this);
        _scrollStoryboard = null;
        ModelNameTransform.X = 0;
        ModelNameText.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private bool IsNameClipped()
    {
        ModelNameText.Measure(new Size(double.PositiveInfinity, NameViewport.ActualHeight));
        return NameViewport.ActualWidth > 0 && ModelNameText.DesiredSize.Width > NameViewport.ActualWidth + 0.5;
    }

    private static string BuildToolTip(ModelProfile profile)
    {
        var host = Uri.TryCreate(profile.ApiBaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "未知主机";
        var status = profile.IsComplete
            ? string.Empty
            : "\n状态：配置不完整，请在设置中补全";
        return $"{profile.SelectorDisplayName}\n模型：{profile.ModelName}\n供应商：{profile.ProviderName}\n主机：{host}{status}";
    }

    private void SelectorButton_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        SelectorButton.ToolTip = _currentProfile is null ? "选择当前会话模型" : BuildToolTip(_currentProfile);
    }

    private sealed record ModelMenuEntry(ModelProfile Profile, bool IsCurrent, string ToolTip)
    {
        public string Id => Profile.Id;
        public string Title => Profile.SelectorDisplayName;
        public string Detail => Profile.MenuDetail;
        public bool IsComplete => Profile.IsComplete;
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    }
}
