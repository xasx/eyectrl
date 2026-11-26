namespace EyeCtrl.App.Services.Coordinators;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the fullscreen Backstage menu's screen claims and lifecycle (Ticket 03). It is a sibling of
/// <see cref="SettingsUiCoordinator"/>, deliberately separate: Settings owns the first-run guide,
/// the toolbar snapshot rebuild and its model-service dependencies, none of which Backstage needs.
///
/// <para>
/// <b>Open claims, in order</b> (mirroring the plan and the Task Switcher precedent): dismiss the
/// transient peers, acquire <see cref="FixationSuppressor.Backstage"/>, set
/// <see cref="ModalInteractionState.BackstageOpen"/>, hold toolbar auto-hide and quick-actions
/// under the <c>"backstage"</c> holder, disarm toolbar dwell (the covered band must not dwell
/// through the menu), hide the cursor ring, then show the window. The open is refused while the
/// sub-pixel probe, the first-run guide or an eye-care exercise owns the screen — a surface that
/// looks reachable but whose dwell is dead would strand a gaze-only owner.
/// </para>
///
/// <para>
/// <b>Close claims, in deterministic reverse order</b>: unregister the window's restart-notice
/// handlers, stop the supervision refresher, clear <see cref="ModalInteractionState.BackstageOpen"/>,
/// release the fixation claim, release both holders, re-arm toolbar dwell with
/// <c>freshEntry: true</c> if the band is on screen, then restore the cursor ring only if no other
/// modal owns the screen. Every path — card action, Esc, Close disc, window Closed, or an exception
/// during the open — runs the same unwind, so a failed open cannot leave a half-held screen.
/// </para>
/// </summary>
internal sealed class BackstageUiCoordinator : CoordinatorBase
{
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly Func<bool> _isExerciseActive;

    private BackstageViewModel? _viewModel;
    private BackstageWindow? _window;
    private AppRestartCoordinator? _restartCoordinator;
    private SupervisionToggleService? _supervisionToggle;
    private Func<ISetupGateways>? _setupGatewaysFactory;
    private ISetupGateways? _openGateways;
    private IUninstallHandoffPresenter? _uninstallHandoff;
    private IUninstallHandoffWatchFactory? _uninstallWatches;
    // Ticket 04: the open window as the uninstall hand-off drives it, and whether THIS hand-off
    // hid it. The window is HIDDEN, never closed, so its layout and view model survive; the flag
    // is what makes the owner's manual reopen cancel the watch instead of showing a second time.
    private IUninstallHandoffWindowHandle? _handoffWindow;
    private bool _handoffHidden;
    private Action? _shutdown;
    private SupervisionPresenceRefresher? _presenceRefresher;
    private SupervisionPresenceRefresher? _lastPresenceRefresher;
    private Action? _presenceTick;
    private readonly string? _assemblyVersion;

    private bool _open;
    private bool _unwinding;

    public BackstageUiCoordinator(
        ModalInteractionState modal,
        QuickActionsCoordinator quickActions,
        ToolbarLifecycleCoordinator toolbar,
        Func<bool> isExerciseActive)
    {
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _isExerciseActive = isExerciseActive ?? throw new ArgumentNullException(nameof(isExerciseActive));
        _assemblyVersion = typeof(BackstageUiCoordinator).Assembly.GetName().Version?.ToString();
    }

    /// <summary>Hands the restart coordinator in; called once at startup through EventCoordinator.</summary>
    internal void SetRestartCoordinator(AppRestartCoordinator coordinator) =>
        _restartCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>Hands the supervision service in; called once at startup.</summary>
    internal void SetSupervisionToggle(SupervisionToggleService service) =>
        _supervisionToggle = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>
    /// Hands the C9 setup-gateways factory in. A fresh instance is created per Backstage visit, so
    /// the Installed/UIAccess facts are re-probed without an App restart (the review-01 M1
    /// invariant).
    /// </summary>
    internal void SetSetupGateways(Func<ISetupGateways> factory) =>
        _setupGatewaysFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>
    /// Test seam: hands the uninstall presenter in. Production builds it lazily over this
    /// coordinator's hide/restore delegates, so the real hide-watch-restore loop is what the
    /// production composition runs.
    /// </summary>
    internal void SetUninstallHandoff(IUninstallHandoffPresenter presenter) =>
        _uninstallHandoff = presenter ?? throw new ArgumentNullException(nameof(presenter));

    /// <summary>
    /// Test seam: the one-shot watch factory the lazily built presenter uses. Production is
    /// <see cref="UninstallHandoffWatchFactory.CreateForProduction" />; a test supplies a watch it
    /// can end on demand, and still drives the REAL presenter.
    /// </summary>
    internal void SetUninstallHandoffWatches(IUninstallHandoffWatchFactory watches) =>
        _uninstallWatches = watches ?? throw new ArgumentNullException(nameof(watches));

    /// <summary>Test/diagnostic seam: whether the hand-off currently owns the hidden window.</summary>
    internal bool HandoffHiddenForTest => _handoffHidden;

    /// <summary>
    /// The presenter the next Backstage visit uses. Built once, lazily; every hand-off creates a
    /// fresh one-shot watch. Ticket 04 moved this ownership here from Settings, which no longer
    /// owns any install surface.
    /// </summary>
    private IUninstallHandoffPresenter UninstallHandoff =>
        _uninstallHandoff ??= new UninstallHandoffPresenter(
            HideBackstageForUninstallHandoff,
            RestoreBackstageAfterUninstallHandoff,
            _uninstallWatches ?? UninstallHandoffWatchFactory.CreateForProduction(),
            warn: line => Log.Warning("{UninstallHandoff}", line));

    /// <summary>Hands in <c>Application.Current.Shutdown</c> (a seam so tests never shut down).</summary>
    internal void SetShutdown(Action shutdown) =>
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));

    /// <summary>Test/diagnostic seam: whether Backstage currently owns the screen.</summary>
    internal bool IsOpen => _open;

    /// <summary>The live window, or null when closed (test seam).</summary>
    internal BackstageWindow? WindowForTest => _window;

    /// <summary>Test seam: whether the one 2 s refresher is actually running. The clock rides it,
    /// so this is also the "no clock still ticking after close" proof. It reads the stopped
    /// instance kept by <see cref="UnwindOpening"/> as well as the live one: a plain
    /// <c>_presenceRefresher?.IsRunning ?? false</c> would read false after the unwind nulls the
    /// field even if <c>Stop()</c> had been dropped, so the pin could not fail.</summary>
    internal bool PresenceRefresherRunningForTest =>
        _presenceRefresher?.IsRunning ?? _lastPresenceRefresher?.IsRunning ?? false;

    /// <summary>Test seam: runs the exact callback the refresher timer runs, so the clock's
    /// wiring into that poll is exercised without waiting two seconds.</summary>
    internal void TickPresenceForTest() => _presenceTick?.Invoke();

    /// <summary>
    /// Test seam: constructs the window. A test supplies a factory that wraps the real window in
    /// <c>InvisibleTestWindow.Prepare</c>, so the coordinator's own lifecycle can be exercised
    /// without painting a fullscreen surface on the owner's display. Null = production.
    /// </summary>
    internal Func<BackstageWindow>? CreateWindowForTest { get; set; }

    protected override void OnInitialize()
    {
        var context = Context!;
        _viewModel = context.BackstageVM;
        if (_viewModel is null)
        {
            // Production always supplies it (App resolves the DI singleton). A focused test context
            // may omit it; the coordinator then simply never opens rather than throwing at startup.
            Log.Warning("Backstage: no BackstageViewModel was supplied; the menu is disabled for this composition.");
        }

        context.Messenger.Register<BackstageUiCoordinator, BackstageRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(ShowBackstage));
        context.Messenger.Register<BackstageUiCoordinator, CloseBackstageMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(CloseBackstage));

        // The global Esc hatch is the only keyboard route that does not steal focus from the app
        // the owner was in. It arrives on a thread-pool thread; marshal before touching the UI.
        context.EscapeHatch.EscapePressed += OnEscapePressed;
    }

    protected override void OnDispose()
    {
        if (_open)
            UnwindOpening();

        // A watch still waiting when the App tears down must not keep a timer (or a Setup mutex
        // read) alive, and must never try to show a window whose owner is gone.
        _uninstallHandoff?.Dispose();
        _uninstallHandoff = null;

        if (Context is not null)
            Context.EscapeHatch.EscapePressed -= OnEscapePressed;
    }

    private void OnEscapePressed()
    {
        var context = Context;
        if (context is null)
            return;

        context.Dispatcher.BeginInvoke(() =>
        {
            if (!_open)
                return;

            // The modal owns the first Esc: a confirmation must be dismissible without losing the
            // menu that opened it. A second Esc closes Backstage.
            if (_window?.IsModalOpen == true)
                _window.DismissModal();
            else
                CloseBackstage();
        });
    }

    /// <summary>
    /// Raises the fullscreen menu, or ignores the request while another screen-owning surface is
    /// up. Called on the UI thread.
    /// </summary>
    internal void ShowBackstage()
    {
        // The owner's manual reopen during the uninstall hand-off: cancel the watch first, so its
        // later end cannot show the menu a second time, then bring the SAME hidden window back.
        if (_handoffHidden)
        {
            _uninstallHandoff?.CancelWatch();
            RestoreBackstageAfterUninstallHandoff(UninstallHandoffEnd.NotUninstalled, null);
            return;
        }

        if (_open || _window is not null)
            return;

        var context = Context;
        if (context is null || _viewModel is null)
            return;

        if (_modal.ProbeOpen || _modal.GuideOpen || _isExerciseActive())
            return;

        _open = true;
        try
        {
            DismissTransientOverlays(context);

            // A fresh gateways instance per visit; not per 2 s refresh.
            _openGateways = _setupGatewaysFactory?.Invoke();

            context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Backstage);
            _modal.BackstageOpen = true;
            _toolbar.SetAutoHideSuspended("backstage", true);
            _quickActions.SetSummonSuspended("backstage", true);
            context.ToolbarVM.SetActive(false);
            context.OverlayManager.CursorCircle.Hide();

            var window = CreateWindowForTest?.Invoke() ?? new BackstageWindow();
            _window = window;
            _handoffWindow = new BackstageWindowHandle(window);
            window.DismissRequested += OnDismissRequested;
            window.CalibrateRequested += OnCalibrateRequested;
            window.Closed += OnWindowClosed;
            window.Configure(
                context.Messenger,
                _viewModel,
                BuildStatus,
                () => _restartCoordinator?.ShouldOfferSupervisor() ?? false,
                () => _shutdown?.Invoke(),
                startSupervisor => _restartCoordinator?.HandleRestart(startSupervisor),
                TargetLanguage,
                _openGateways,
                UninstallHandoff,
                () => context.InteractionManager.CurrentState == InteractionState.Paused);

            window.ShowBackstage();
            StartPresenceRefresh();
        }
        catch (Exception ex)
        {
            // A raise that fails after some claims were taken must unwind them; rethrowing would
            // land in DispatcherUnhandledException during a user gesture.
            Log.Error(ex, "Backstage: open failed; unwinding the screen claims");
            UnwindOpening();
        }
    }

    /// <summary>Closes the menu and releases every claim. Safe to call twice.</summary>
    internal void CloseBackstage()
    {
        if (!_open && _window is null)
            return;

        UnwindOpening();
    }

    private void OnDismissRequested() => CloseBackstage();

    private void OnCalibrateRequested()
    {
        // Calibration owns its own fixation suppression and cursor-ring lifecycle. Close FIRST so
        // Backstage's teardown cannot collide with the calibration window's claims, then send.
        CloseBackstage();
        Context?.Messenger.Send(new CalibrationRequestedMessage());
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_unwinding)
            return;

        // An unexpected close (Alt+F4, the OS): release the claims exactly once. The window has
        // already run its own OnClosed (notices unregistered, VM detached), so the unwind must not
        // call Hide/Close on it again — WPF refuses window operations after Closed.
        if (_open)
            UnwindOpening(windowAlreadyClosed: true);
    }

    private void OnSupervisionStateChanged() => _window?.RefreshStatus();

    private BackstageStatusView BuildStatus()
    {
        var context = Context;
        return BackstageStatusFacts.Capture(
            _openGateways,
            context?.InputIntegrityProbe,
            _supervisionToggle,
            _assemblyVersion);
    }

    private UiLanguage TargetLanguage() =>
        RestartNotices.TargetLanguage(Context!.Settings.General.UiLanguage);

    // ── Ticket 04: the uninstall hand-off's hide / come-back ────────────────────────────────────
    //
    // The reason is z-order, not taste: Backstage runs in ZBID_UIACCESS (band 2) while Setup runs
    // at uiAccess=false, so Setup's topmost Q8 can never be raised above it. Hiding is the only
    // thing that works, and the menu must be HIDDEN rather than closed so the same session (and
    // its window) comes back on every end that does not remove EyeCtrl.

    /// <summary>
    /// Hides the open menu for the hand-off, releasing every screen claim it holds while open —
    /// <see cref="ModalInteractionState.BackstageOpen"/>, the fixation claim, the toolbar
    /// auto-hide suspension and the quick-actions summon claim — so the owner keeps a usable
    /// toolbar and ring next to Setup's window and gaze can drive its Q8. Returns whether a visible
    /// window was hidden; false means there is nothing to bring back.
    /// </summary>
    internal bool HideBackstageForUninstallHandoff()
    {
        var context = Context;
        if (context is null) return false;
        if (_handoffHidden) return false;
        if (_handoffWindow is not { IsVisible: true } window) return false;

        // Set the flag first: a throw below must not leave the coordinator believing the menu is
        // still on screen when the window has already gone.
        _handoffHidden = true;

        // The menu is no longer on screen, so the 2 s poll has nothing to refresh. Keep the
        // instance so PresenceRefresherRunningForTest can still observe a missed Stop().
        StopPresenceRefresh();

        window.Hide();

        _modal.BackstageOpen = false;
        context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Backstage);
        _toolbar.SetAutoHideSuspended("backstage", false);
        _quickActions.SetSummonSuspended("backstage", false);

        if (context.OverlayManager.Toolbar.IsOnScreen)
            context.ToolbarVM.SetActive(true, freshEntry: true);

        if (context.CursorVM.IsVisible
            && !_modal.SettingsOpen
            && !_modal.ProbeOpen
            && !_modal.GuideOpen)
        {
            context.OverlayManager.CursorCircle.Show();
        }

        Log.Information("Backstage hidden for the uninstall hand-off");
        return true;
    }

    /// <summary>
    /// The watch's end. Only <see cref="UninstallHandoffEnd.Uninstalled" /> keeps the menu hidden:
    /// Setup stops the App in the same run, and a fullscreen menu raised over the removal would be
    /// the last thing the owner sees. Everything else — cancel, „Nein, behalten", Esc, close, the
    /// five-minute timeout, a refusal, a failure, a watcher that never learned the outcome —
    /// re-takes the claims and shows the same window again. A restore that arrives after the owner
    /// already reopened Backstage (or after the window was closed) does nothing.
    /// </summary>
    internal void RestoreBackstageAfterUninstallHandoff(
        UninstallHandoffEnd end, SetupOutcome? outcome = null)
    {
        var context = Context;
        if (context is null) return;

        if (end == UninstallHandoffEnd.Uninstalled)
        {
            Log.Information("Backstage stays hidden: the uninstall completed");
            return;
        }

        if (!_handoffHidden)
        {
            // The owner already reopened it: nothing to show, but the end still belongs in the log.
            Log.Information(
                "Uninstall hand-off ended after the owner reopened Backstage: {End} ({Outcome})",
                end, outcome);
            return;
        }

        if (_handoffWindow is not { } window)
        {
            // The window closed while hidden: the close path has already released every claim.
            _handoffHidden = false;
            return;
        }

        _modal.BackstageOpen = true;
        context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Backstage);
        _toolbar.SetAutoHideSuspended("backstage", true);
        _quickActions.SetSummonSuspended("backstage", true);
        context.ToolbarVM.SetActive(false);
        context.OverlayManager.CursorCircle.Hide();

        try
        {
            window.Show();
            _handoffHidden = false;
        }
        catch (Exception ex)
        {
            // Keep the flag so a later Backstage request retries the show instead of reporting a
            // window that is still hidden as open.
            Log.Warning(ex, "Could not show Backstage after the uninstall hand-off");
            return;
        }

        StartPresenceRefresh();
        Log.Information(
            "Backstage restored after the uninstall hand-off: {End} ({Outcome})", end, outcome);
    }

    /// <summary>
    /// Starts the menu's one 2 s presence refresh. The strip rebuilds only when the supervision
    /// service raises a real change, so it never flaps its pills; the clock rides the same tick
    /// (a minute-resolution clock needs no second timer, and a second timer would be a second
    /// thing to leak on teardown).
    /// </summary>
    private void StartPresenceRefresh()
    {
        _presenceTick = () =>
        {
            _supervisionToggle?.RefreshConnection();
            _window?.RefreshStatus();
            _window?.RefreshClock();
        };
        _presenceRefresher = new SupervisionPresenceRefresher(_presenceTick);
        _presenceRefresher.Start();
        if (_supervisionToggle is not null)
            _supervisionToggle.StateChanged += OnSupervisionStateChanged;
    }

    /// <summary>Stops the presence refresh, keeping the stopped instance observable to the test seam.</summary>
    private void StopPresenceRefresh()
    {
        if (_supervisionToggle is not null)
            _supervisionToggle.StateChanged -= OnSupervisionStateChanged;
        // Keep the instance so the test seam can still observe a missed Stop() after the
        // field below is nulled; a leaked DispatcherTimer would otherwise be invisible.
        _lastPresenceRefresher = _presenceRefresher;
        _presenceRefresher?.Stop();
        _presenceRefresher = null;
        _presenceTick = null;
    }

    /// <summary>
    /// Dismisses the transient overlays a fullscreen menu must not be painted over. Each call is
    /// the one its own owner already exposes; Settings is the exception — it closes through its
    /// message, and its Closed handler is made Backstage-aware so it cannot restore the cursor ring
    /// under this menu.
    /// </summary>
    private static void DismissTransientOverlays(EventCoordinationContext context)
    {
        context.Messenger.Send(new CloseSettingsMessage());
        context.Messenger.Send(new HideKeyboardMessage());
        context.ModesMenuVM.HideOverlay();
        context.TaskSwitcherVM.HideOverlay();
        context.QuickActionsGridVM.HideOverlay();
        context.ActionMenuVM.IsVisible = false;
        context.OverlayManager.GlassBackdrop.Hide();
        context.OverlayManager.GlassBackdrop.CompleteHideNow();
    }

    /// <summary>
    /// The one teardown, in the order the plan fixes. Idempotent; every step tolerates a claim it
    /// never took (release-without-claim is a no-op on all three).
    /// </summary>
    private void UnwindOpening(bool windowAlreadyClosed = false)
    {
        if (_unwinding)
            return;

        _unwinding = true;
        try
        {
            var context = Context;

            // Closing while the hand-off hid the menu: the watch must not raise a window the close
            // path is about to close for good.
            if (_handoffHidden)
            {
                _uninstallHandoff?.CancelWatch();
                _handoffHidden = false;
            }

            _handoffWindow = null;
            StopPresenceRefresh();

            var window = _window;
            _window = null;
            if (window is not null)
            {
                window.DismissRequested -= OnDismissRequested;
                window.CalibrateRequested -= OnCalibrateRequested;
                window.Closed -= OnWindowClosed;
                if (!windowAlreadyClosed)
                    window.HideBackstage();      // unregisters the restart notices first
            }

            _viewModel?.HideOverlay();

            _modal.BackstageOpen = false;
            context?.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Backstage);
            _toolbar.SetAutoHideSuspended("backstage", false);
            _quickActions.SetSummonSuspended("backstage", false);

            if (context is not null && context.OverlayManager.Toolbar.IsOnScreen)
                context.ToolbarVM.SetActive(true, freshEntry: true);

            // The ring comes back only when no other modal owns the screen. In particular the
            // Settings window's Closed handler (posted by CloseSettingsMessage) may run after this
            // unwind; it checks BackstageOpen itself, which this method has just cleared.
            if (context is not null
                && context.CursorVM.IsVisible
                && !_modal.SettingsOpen
                && !_modal.ProbeOpen
                && !_modal.GuideOpen)
            {
                context.OverlayManager.CursorCircle.Show();
            }

            _openGateways = null;
        }
        finally
        {
            _open = false;
            _unwinding = false;
        }
    }
}
