using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        IsEnabled = enabled;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ModelNameText.Text = currentProfile?.DisplayName ?? "选择模型";
        SelectorButton.ToolTip = currentProfile is null
            ? "选择当前会话模型"
            : BuildToolTip(currentProfile);
        ResetNameScroll();
    }

    private void SelectorButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SelectorButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x38)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xF4))
        };
        foreach (var profile in _profiles)
        {
            var item = new MenuItem
            {
                Header = CreateMenuHeader(profile),
                IsEnabled = profile.IsComplete,
                IsCheckable = true,
                IsChecked = _currentProfile?.Id == profile.Id,
                ToolTip = BuildToolTip(profile)
            };
            var selectedId = profile.Id;
            item.Click += (_, _) => ProfileSelected?.Invoke(selectedId);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var settingsItem = new MenuItem { Header = "管理模型配置..." };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);
        menu.Opened += (_, _) => MenuOpened?.Invoke();
        menu.Closed += (_, _) => MenuClosed?.Invoke();
        menu.IsOpen = true;
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
        return $"{profile.DisplayName}\n模型：{profile.ModelName}\n供应商：{profile.ProviderName}\n主机：{host}";
    }

    private static FrameworkElement CreateMenuHeader(ModelProfile profile)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 1, 8, 1) };
        panel.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{profile.ModelName} · {profile.ProviderName}",
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 10,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xA8, 0xA8, 0xB8))
        });
        return panel;
    }

    private void SelectorButton_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        SelectorButton.ToolTip = _currentProfile is null ? "选择当前会话模型" : BuildToolTip(_currentProfile);
    }
}
