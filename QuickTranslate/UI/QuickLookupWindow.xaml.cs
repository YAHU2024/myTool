using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

public partial class QuickLookupWindow : Window
{
    private readonly IWordLookupService _lookupService;
    private readonly IWordLookupEnrichmentService _enrichmentService;
    private readonly WordLookupSessionCoordinator _sessions;
    private readonly RecentLookupBuffer _recent;
    private readonly TtsPlaybackCoordinator _tts;
    private string _explanationLanguage;
    private bool _ttsEnabled;
    private string _ttsVoice = string.Empty;
    private double _ttsRate = 1;
    private int _ttsMaxChars = 2000;
    private bool _isImeComposing;
    private bool _isClosingForExit;
    private CancellationTokenSource? _enrichmentCts;
    private readonly DispatcherTimer _feedbackTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    public QuickLookupWindow(
        IWordLookupService lookupService,
        IWordLookupEnrichmentService enrichmentService,
        WordLookupSessionCoordinator sessions,
        RecentLookupBuffer recent,
        TtsPlaybackCoordinator tts,
        string explanationLanguage)
    {
        _lookupService = lookupService ?? throw new ArgumentNullException(nameof(lookupService));
        _enrichmentService = enrichmentService ?? throw new ArgumentNullException(nameof(enrichmentService));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _recent = recent ?? throw new ArgumentNullException(nameof(recent));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _explanationLanguage = explanationLanguage;
        InitializeComponent();

        _sessions.StateChanged += OnSessionStateChanged;
        _tts.StateChanged += OnTtsStateChanged;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(
            () =>
            {
                if (!_isImeComposing)
                    DeactivationRequested?.Invoke();
            },
            DispatcherPriority.Input);
        Closing += (_, e) =>
        {
            if (_isClosingForExit)
                return;
            e.Cancel = true;
            HidePanel();
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(QueryTextBox, OnTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(QueryTextBox, OnTextInputUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(QueryTextBox, OnTextInputCompleted);
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            FeedbackBar.Visibility = Visibility.Collapsed;
        };
        Render(_sessions.Current);
        RefreshRecentItems();
    }

    public event Action? HideRequested;
    public event Action? DeactivationRequested;

    public void ApplySettings(
        string explanationLanguage,
        bool ttsEnabled,
        string? voice,
        double rate,
        int maxChars)
    {
        _explanationLanguage = string.IsNullOrWhiteSpace(explanationLanguage)
            ? "简体中文"
            : explanationLanguage.Trim();
        _ttsEnabled = ttsEnabled;
        _ttsVoice = voice?.Trim() ?? string.Empty;
        _ttsRate = rate;
        _ttsMaxChars = maxChars > 0 ? maxChars : 2000;
        RefreshSpeakButtons(_tts.Current);
    }

    public void PrepareForShow()
    {
        if (!IsVisible)
            Show();
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            QueryTextBox.Focus();
            QueryTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void HidePanel()
    {
        CancelEnrichment();
        _ = _tts.StopAsync(TtsPlaybackOwner.QuickLookup);
        Hide();
        HideRequested?.Invoke();
    }

    public void CloseForExit()
    {
        _isClosingForExit = true;
        _sessions.StateChanged -= OnSessionStateChanged;
        _tts.StateChanged -= OnTtsStateChanged;
        CancelEnrichment();
        Close();
    }

    private async Task SubmitAsync(string rawQuery)
    {
        CancelEnrichment();
        string query;
        try
        {
            query = WordLookupPromptBuilder.NormalizeQuery(rawQuery);
        }
        catch (ArgumentException ex)
        {
            ShowMessage("无法查询", ex.Message, canRetry: false);
            return;
        }

        QueryTextBox.Text = query;
        QueryTextBox.CaretIndex = query.Length;
        var scope = _sessions.Begin(query);
        try
        {
            var result = await _lookupService.LookupAsync(
                new WordLookupRequest(query, _explanationLanguage),
                scope.Token).ConfigureAwait(true);
            if (_sessions.TryComplete(scope, result))
            {
                _recent.AddSuccessful(query);
                RefreshRecentItems();
            }
        }
        catch (WordLookupNotFoundException)
        {
            _sessions.TryNotFound(scope);
        }
        catch (OperationCanceledException)
        {
            _sessions.TryCancel(scope);
        }
        catch (WordLookupFormatException)
        {
            _sessions.TryFail(scope, "服务返回的数据格式无效，请重试。");
        }
        catch (HttpRequestException)
        {
            _sessions.TryFail(scope, "网络或查词服务暂时不可用，请稍后重试。");
        }
        catch (InvalidOperationException)
        {
            _sessions.TryFail(scope, "请检查 API 地址、Key 和模型设置。");
        }
        catch (Exception)
        {
            _sessions.TryFail(scope, "查询失败，请稍后重试。");
        }
    }

    private void OnSessionStateChanged(WordLookupSessionState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSessionStateChanged(state));
            return;
        }
        Render(state);
    }

    private void Render(WordLookupSessionState state)
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;

        switch (state.Status)
        {
            case WordLookupSessionStatus.Empty:
                EmptyPanel.Visibility = Visibility.Visible;
                break;
            case WordLookupSessionStatus.Loading:
                LoadingPanel.Visibility = Visibility.Visible;
                break;
            case WordLookupSessionStatus.Completed when state.Result is not null:
                RenderResult(state.Result);
                break;
            case WordLookupSessionStatus.NotFound:
                ShowMessage("未找到释义", "请检查拼写，或尝试更短的单词和短语。", canRetry: true);
                break;
            case WordLookupSessionStatus.Failed:
                ShowMessage("查询失败", state.ErrorMessage ?? "请稍后重试。", canRetry: true);
                break;
            case WordLookupSessionStatus.Cancelled:
                if (_sessions.Current.Result is not null)
                    RenderResult(_sessions.Current.Result);
                else
                    EmptyPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    private void RenderResult(WordLookupResult result)
    {
        ResultPanel.Visibility = Visibility.Visible;
        HeadwordText.Text = result.Headword;
        PronunciationsItems.ItemsSource = result.Pronunciations;
        PronunciationsItems.Visibility = result.Pronunciations.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SensesItems.ItemsSource = result.Senses;
        ExamplesItems.ItemsSource = result.Examples;
        CollocationsItems.ItemsSource = result.Collocations;
        CollocationsPanel.Visibility = result.Collocations.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SourceText.Text = result.Source.DisplayName;
        EnrichButton.Visibility = result.Source.Kind == WordLookupSourceKind.Dictionary &&
                                  HasMissingChinese(result)
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnrichButton.IsEnabled = true;
        ResultScroller.ScrollToTop();
        RefreshSpeakButtons(_tts.Current);
    }

    private void ShowMessage(string title, string body, bool canRetry)
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageBody.Text = body;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRecentItems() => RecentItems.ItemsSource = _recent.Items;

    private async void EnrichButton_Click(object sender, RoutedEventArgs e)
    {
        var state = _sessions.Current;
        if (state is not { Status: WordLookupSessionStatus.Completed, Result: { } localResult } ||
            localResult.Source.Kind != WordLookupSourceKind.Dictionary ||
            !HasMissingChinese(localResult))
        {
            return;
        }

        CancelEnrichment();
        var cts = new CancellationTokenSource();
        _enrichmentCts = cts;
        EnrichButton.IsEnabled = false;
        try
        {
            var enriched = await _enrichmentService.EnrichAsync(
                new WordLookupRequest(state.Query, _explanationLanguage),
                localResult,
                cts.Token).ConfigureAwait(true);
            if (_sessions.TryReplaceCompletedResult(state.RequestId, enriched))
                ShowTransientFeedback("AI 中文补全完成。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            ShowTransientFeedback("请先在设置中填写可用的 API Key 和模型。");
        }
        catch (WordLookupFormatException)
        {
            ShowTransientFeedback("AI 补全结果格式无效，请重试。");
        }
        catch (HttpRequestException)
        {
            ShowTransientFeedback("AI 补全服务暂时不可用，请稍后重试。");
        }
        catch (Exception)
        {
            ShowTransientFeedback("AI 补全失败，请稍后重试。");
        }
        finally
        {
            if (ReferenceEquals(_enrichmentCts, cts))
            {
                _enrichmentCts = null;
                cts.Dispose();
                var current = _sessions.Current.Result;
                EnrichButton.IsEnabled = true;
                EnrichButton.Visibility = current is not null &&
                                          current.Source.Kind == WordLookupSourceKind.Dictionary &&
                                          HasMissingChinese(current)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private static bool HasMissingChinese(WordLookupResult result) =>
        result.Senses.Any(sense =>
            string.IsNullOrWhiteSpace(sense.Definition) &&
            !string.IsNullOrWhiteSpace(sense.EnglishDefinition)) ||
        result.Examples.Any(example => string.IsNullOrWhiteSpace(example.Translation));

    private void CancelEnrichment()
    {
        var source = Interlocked.Exchange(ref _enrichmentCts, null);
        source?.Cancel();
        source?.Dispose();
    }

    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_ttsEnabled)
            return;
        if (_tts.IsBusy(TtsPlaybackOwner.QuickLookup))
        {
            await _tts.StopAsync(TtsPlaybackOwner.QuickLookup).ConfigureAwait(true);
            return;
        }

        var text = sender is Button { Tag: string tagged } && !string.IsNullOrWhiteSpace(tagged)
            ? tagged
            : _sessions.Current.Result?.Headword;
        if (string.IsNullOrWhiteSpace(text))
            return;
        var speech = TtsTextSelector.NormalizeForSpeech(text, _ttsMaxChars, out _);
        try
        {
            await _tts.SpeakAsync(
                TtsPlaybackOwner.QuickLookup,
                speech,
                null,
                string.IsNullOrWhiteSpace(_ttsVoice) ? null : _ttsVoice,
                _ttsRate,
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TtsSpeakException ex) when (ex.ErrorKind == TtsSpeakException.Cancelled)
        {
        }
        catch (Exception)
        {
            ShowTransientFeedback("朗读失败，语音服务暂时不可用。");
        }
    }

    private void OnTtsStateChanged(TtsPlaybackState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTtsStateChanged(state));
            return;
        }
        RefreshSpeakButtons(state);
    }

    private void RefreshSpeakButtons(TtsPlaybackState state)
    {
        if (SpeakHeadwordButton is null)
            return;
        var ownBusy = state.IsBusy && state.Owner == TtsPlaybackOwner.QuickLookup;
        SpeakHeadwordButton.Content = ownBusy ? "\uE71A" : "\uE767";
        SpeakHeadwordButton.ToolTip = ownBusy ? "停止朗读" : "朗读词头";
        SpeakHeadwordButton.IsEnabled = _ttsEnabled && _sessions.Current.Result is not null;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.Current.Result is not { } result)
            return;
        try
        {
            Clipboard.SetText(WordLookupTextFormatter.Format(result));
            TransientButtonFeedback.ShowCopySuccess(CopyButton, "\uE8C8");
        }
        catch
        {
            ShowMessage("复制失败", "剪贴板当前不可用，请稍后重试。", canRetry: false);
        }
    }

    private void ShowTransientFeedback(string message)
    {
        FeedbackText.Text = message;
        FeedbackBar.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e) => _ = SubmitAsync(QueryTextBox.Text);
    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = SubmitAsync(QueryTextBox.Text);

    private void RecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string query })
            return;
        QueryTextBox.Text = query;
        _ = SubmitAsync(query);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void QueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputHintText.Visibility = string.IsNullOrEmpty(QueryTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter &&
            QueryTextBox.IsKeyboardFocusWithin &&
            !_isImeComposing &&
            e.ImeProcessedKey == Key.None)
        {
            _ = SubmitAsync(QueryTextBox.Text);
            e.Handled = true;
        }
    }

    private void OnTextInputStart(object sender, TextCompositionEventArgs e) => _isImeComposing = true;
    private void OnTextInputUpdate(object sender, TextCompositionEventArgs e) => _isImeComposing = true;
    private void OnTextInputCompleted(object sender, TextCompositionEventArgs e) => _isImeComposing = false;
}
