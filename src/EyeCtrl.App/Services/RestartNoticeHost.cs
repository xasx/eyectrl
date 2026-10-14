namespace EyeCtrl.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Enums;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// The App-level home of the restart notices (holistic review F5). A restart can begin with no
/// Settings window open: the Supervisor's language row asks the App to restart, and the caretaker
/// typically acts with Settings closed. The "restarting" and "restart not possible" notices used to
/// have only the Settings window's handlers, so a refused restart was invisible on that path and the
/// App kept the old language with no word to anyone.
///
/// <para>
/// This host shows exactly the same notices (the same keys, through <see cref="RestartNotices"/>, in
/// the same target language) when no Settings window is open, and stays silent when one is: the open
/// window shows its own modal, so the notice never appears twice. The host owns no UI; it hands the
/// text to an <see cref="IRestartNoticePresenter"/>, and forwards gaze to it while it shows.
/// </para>
/// </summary>
public sealed class RestartNoticeHost : IDisposable
{
    /// <summary>
    /// How long "restarting" stays when nobody closes it: past the 30 s the replacement is given to
    /// announce itself. Success takes the process down with it; failure replaces the notice with the
    /// refusal at once, so this is only a backstop.
    /// </summary>
    internal static readonly TimeSpan InProgressHold = TimeSpan.FromSeconds(40);

    /// <summary>How long "restart not possible" stays when nobody dwells OK.</summary>
    internal static readonly TimeSpan RefusedHold = TimeSpan.FromSeconds(30);

    private readonly IMessenger _messenger;
    private readonly IRestartNoticePresenter _presenter;
    private readonly Func<bool> _isSettingsOpen;
    private readonly Func<UiLanguage> _targetLanguage;
    private readonly Action<Action> _postToUi;

    /// <summary>Registers the host on the messenger.</summary>
    /// <param name="messenger">The application messenger the restart coordinator sends on.</param>
    /// <param name="presenter">The notice renderer (the WPF window at runtime, a fake in tests).</param>
    /// <param name="isSettingsOpen">Whether a Settings window exists; it then shows its own modal.</param>
    /// <param name="targetLanguage">The language the restart flow speaks (the saved selection resolved
    /// against the machine culture, the language the replacement will run).</param>
    /// <param name="postToUi">Runs work on the UI thread.</param>
    public RestartNoticeHost(
        IMessenger messenger,
        IRestartNoticePresenter presenter,
        Func<bool> isSettingsOpen,
        Func<UiLanguage> targetLanguage,
        Action<Action> postToUi)
    {
        _messenger = messenger;
        _presenter = presenter;
        _isSettingsOpen = isSettingsOpen;
        _targetLanguage = targetLanguage;
        _postToUi = postToUi;

        messenger.Register<RestartInProgressMessage>(this, (_, _) => _postToUi(ShowInProgress));
        messenger.Register<RestartUnavailableMessage>(this, (_, m) => _postToUi(() => ShowRefused(m.Reason)));
        // A held (gap) sample must not accumulate dwell. Paused does not gate this: closing a notice
        // injects no input, so, like the toolbar band, the close button stays dwellable while paused.
        messenger.Register<GazeCursorMovedMessage>(this, (_, m) =>
        {
            if (_presenter.IsShowing && !m.IsInGap && !_isSettingsOpen())
                _presenter.OnGaze(m.Position, m.TimestampMicroseconds);
        });
    }

    private void ShowInProgress()
    {
        if (_isSettingsOpen()) return; // the open Settings window shows its own modal

        var target = _targetLanguage();
        Log.Information("Restart notice: showing 'restarting' (no Settings window is open)");
        _presenter.ShowNotice(
            RestartNotices.InProgressMessage(target),
            RestartNotices.InProgressChoices(target)[0].Label,
            InProgressHold);
    }

    private void ShowRefused(string reason)
    {
        if (_isSettingsOpen()) return;

        var target = _targetLanguage();
        Log.Information("Restart notice: showing 'restart not possible' (no Settings window is open)");
        _presenter.ShowNotice(reason, RestartNotices.RefusedChoices(target)[0].Label, RefusedHold);
    }

    /// <summary>
    /// Settings opened or closed. Opening takes a notice that is up down: the Settings window now
    /// covers the card, whose close button would otherwise stay live under it, an invisible target
    /// whose dwell would play the activation cue for nothing the user can see.
    /// </summary>
    /// <param name="open">True when a Settings window has opened.</param>
    public void OnSettingsOpenChanged(bool open)
    {
        if (open) _presenter.HideNotice();
    }

    /// <summary>Unregisters from the messenger and takes any notice down.</summary>
    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _presenter.HideNotice();
    }
}
