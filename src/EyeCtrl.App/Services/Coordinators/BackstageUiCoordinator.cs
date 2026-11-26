namespace EyeCtrl.App.Services.Coordinators;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Localization;
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
                TargetLanguage);

            window.ShowBackstage();

            // The presence refresh only raises SupervisionToggleService.StateChanged on a real
            // change, so the strip rebuilds at most twice a second and never flaps its pills. The
            // clock rides the same tick: a minute-resolution clock needs no second timer, and a
            // second timer would be a second thing to leak on teardown.
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

            if (_supervisionToggle is not null)
                _supervisionToggle.StateChanged -= OnSupervisionStateChanged;
            // Keep the instance so the test seam can still observe a missed Stop() after the
            // field below is nulled; a leaked DispatcherTimer would otherwise be invisible.
            _lastPresenceRefresher = _presenceRefresher;
            _presenceRefresher?.Stop();
            _presenceRefresher = null;
            _presenceTick = null;

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
