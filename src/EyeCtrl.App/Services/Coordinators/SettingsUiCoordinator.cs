namespace EyeCtrl.App.Services.Coordinators;

using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the settings-UI surface of <see cref="EventCoordinator"/>: the settings window lifecycle
/// (<see cref="ShowSettingsWindow"/> via the <see cref="SettingsRequestedMessage"/> registration,
/// <see cref="OnSettingsWindowClosed"/>, the <see cref="CloseSettingsMessage"/> registration), the
/// settings-applied reaction (<see cref="OnSettingsChanged"/>: toolbar re-init + keyboard reload),
/// the track-status visibility binding, and the model/restart-coordinator hand-off. Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (cluster 7).
/// </summary>
internal sealed class SettingsUiCoordinator : CoordinatorBase
{
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly KeyboardCoordinator _keyboard;
    private SettingsWindow? _settingsWindow;
    private ModelRegistryService? _modelRegistry;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;
    private SupervisionToggleService? _supervisionToggle;
    private Func<ISetupGateways>? _setupGatewaysFactory;
    private IUninstallHandoffPresenter? _uninstallHandoff;
    // C9 S6e fixup-08: the open window as the uninstall hand-off drives it, and whether THIS
    // hand-off hid it. Both are cleared when the window closes, so a watch that outlives it
    // cannot show a window nobody owns.
    private ISettingsWindowHandle? _handoffWindow;
    private bool _settingsHiddenForUninstallHandoff;
    // The probe entry (build plan C10): the Diagnostics button's reason probe and request action,
    // handed over once at startup by EventCoordinator after C7 exists.
    private Func<string?>? _probeBlockReason;
    private Action? _probeRequest;
    private Func<bool>? _probeCanResume;
    // Owned HERE — moved from EventCoordinator. The metrics snapshot AND the includeReset flag
    // (Adaptation.Enabled) of the last band build, compared by ReinitializeToolbar so the save
    // path and the live Enabled subscription (Task 9) rebuild the band exactly once however
    // their arrivals interleave.
    private ToolbarSnapshot? _toolbarSnapshot;
    private bool? _appliedIncludeReset;
    public SettingsUiCoordinator(ModalInteractionState modal, QuickActionsCoordinator quickActions,
        ToolbarLifecycleCoordinator toolbar, KeyboardCoordinator keyboard)
    {
        // All four are required collaborators dereferenced unconditionally (the suspend seams and the
        // ReloadIfChanged seam), so reject nulls at the ctor rather than at first use.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    }

    protected override void OnInitialize()
    {
        var context = Context!;

        var messenger = context.Messenger;

        // Settings window
        messenger.Register<SettingsUiCoordinator, SettingsRequestedMessage>(this, (_, m) =>
            context.Dispatcher.BeginInvoke(() => ShowSettingsWindow(m.Target)));

        // Startup safety setup: open the window if it is not open, then start the chain on it.
        messenger.Register<SettingsUiCoordinator, SafetySetupRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(ShowSafetySetup));

        // CloseSettings -> close settings window
        messenger.Register<SettingsUiCoordinator, CloseSettingsMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() =>
            {
                if (_settingsWindow != null)
                {
                    _settingsWindow.Close();
                }
            }));

        UpdateTrackStatusVisibility();
        // Named handler (was an anonymous lambda that was never detached — it kept this coordinator
        // alive through the settings object, which outlives EventCoordinator). Dispose -= it.
        context.Settings.TrackStatus.PropertyChanged += OnTrackStatusSettingsPropertyChanged;
        // Task 9: the band's Reset membership follows the live Adaptation.Enabled flip. The
        // settings UI mutates the live object as the user toggles — before any save raises
        // SettingsChanged — so the flip arrives here as PropertyChanged. Marshalled like the
        // TrackStatus handler above; the rebuild itself lives in ReinitializeToolbar.
        context.Settings.Adaptation.PropertyChanged += OnAdaptationSettingsPropertyChanged;
    }

    private void OnTrackStatusSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName == nameof(TrackStatusSettings.Enabled))
            Context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
    }

    private void OnAdaptationSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName != nameof(AdaptationSettings.Enabled)) return;
        // Through the same guard the save path uses, so a flip that also lands in a save
        // re-initializes the band exactly once.
        var settings = Context.Settings;
        Context.Dispatcher.BeginInvoke(() => ReinitializeToolbar(settings));
    }

    /// <summary>Stores the model services for the next <see cref="ShowSettingsWindow"/> construction.
    /// 2-arg (matches KeyboardCoordinator + EC's forwarder; the dead download service is dropped at the seam).</summary>
    public void SetModelServices(ModelRegistryService registry, ModelManagementService management)
    {
        _modelRegistry = registry;
        _modelManagementService = management;
    }

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _restartCoordinator = coordinator;

    /// <summary>Hands the supervision toggle (ticket 04) to the settings window at construction.
    /// Called once at startup, like <see cref="SetRestartCoordinator"/>.</summary>
    public void SetSupervisionToggle(SupervisionToggleService service) => _supervisionToggle = service;

    /// <summary>Hands the S6f setup-gateways FACTORY (the „Erweitert" update/rollback/uninstall
    /// rows) to the settings window construction. Called once at startup, like
    /// <see cref="SetSupervisionToggle"/>; null keeps the page free of the rows. A factory, not an
    /// instance: review 01 M1 requires a fresh offer and policy per Settings window, so an inbox
    /// change or a caregiver fixing the policy shows on the next visit without an App restart.</summary>
    public void SetSetupGateways(Func<ISetupGateways> gatewaysFactory) =>
        _setupGatewaysFactory = gatewaysFactory;

    /// <summary>
    /// Hands the sub-pixel probe entry (build plan C10) to the next settings window: the
    /// precondition reason the Diagnostics button shows on activation, and the request action that
    /// closes Settings and queues the probe start. Called once at startup, after C7 exists.
    /// </summary>
    /// <param name="probeBlockReason">Returns the localized refusal, or <c>null</c> when start is allowed.</param>
    /// <param name="requestProbe">Closes Settings synchronously and queues the probe start.</param>
    /// <param name="canResumeSession2">Whether a session-2 resume applies; SP-6 supplies the reader.</param>
    public void SetProbeEntry(
        Func<string?> probeBlockReason, Action requestProbe, Func<bool>? canResumeSession2 = null)
    {
        _probeBlockReason = probeBlockReason ?? throw new ArgumentNullException(nameof(probeBlockReason));
        _probeRequest = requestProbe ?? throw new ArgumentNullException(nameof(requestProbe));
        _probeCanResume = canResumeSession2;
    }

    /// <summary>
    /// Closes the Settings window synchronously for a probe request. The window's <c>Closed</c>
    /// handler raises <c>SettingsOpenChanged(false)</c> inside <c>Close()</c>, which is what C7's
    /// arming depends on (review 05 F8).
    /// </summary>
    internal void CloseSettingsWindowForProbe()
    {
        if (_settingsWindow is not null)
            _settingsWindow.Close();
    }

    /// <summary>Gaze-routing seam (consumed by ToolbarLifecycleCoordinator.SetupGazeRouting after
    /// the cluster-8 fold): forwards the sample to the settings window iff it is open and visible.
    /// The visibility check lives here because the <c>_settingsWindow</c> reference moved out of EC
    /// with this coordinator.</summary>
    internal void UpdateSettingsGazeIfVisible(PhysicalPoint point, long timestamp)
    {
        if (_settingsWindow?.IsVisible ?? false)
            _settingsWindow.UpdateGaze(point, timestamp);
    }

    private void UpdateTrackStatusVisibility()
    {
        if (Context is null) return;
        var trackStatusWindow = Context.OverlayManager.TrackStatus;
        if (Context.Settings.TrackStatus.Enabled)
            trackStatusWindow.StartTracking();
        else
            trackStatusWindow.StopTracking();
    }

    /// <summary>
    /// Opens settings, or applies a deep link to the window that is already open. There is no
    /// branch here: <see cref="SettingsDeepLink.Dispatch"/> decides (pure, unit-tested), turns the
    /// action into a plan and runs it — open first, then the fresh navigation, so the navigation
    /// lands after construction without its own guard. The modal case is the window's own queue.
    /// </summary>
    /// <param name="target">Where the request should land; <see cref="SettingsTarget.Default"/>
    /// keeps the historical "open where it opens" behaviour and never touches an open window.</param>
    private void ShowSettingsWindow(SettingsTarget target = SettingsTarget.Default)
    {
        if (Context is null) return;

        // C9 S6e fixup-08: a Settings request while the window is hidden for the uninstall
        // hand-off is the owner bringing it back manually (the toolbar, the tray, a keyboard
        // row). Show it now and cancel the automatic return — otherwise the watch would raise it
        // a second time when Setup ends. The hidden window kept its tab, so nothing is lost.
        ReopenHiddenSettingsWindowForUninstallHandoff();

        SettingsDeepLink.Dispatch(
            windowOpen: _settingsWindow is not null,
            target: target,
            openWindow: OpenSettingsWindow,
            navigate: () => _settingsWindow?.NavigateToLanguageRow());
    }

    /// <summary>Opens settings when it is not open, then starts the safety chain on the window; a
    /// window this opened closes again when the chain ends. The decision is SafetySetupLaunch's.</summary>
    private void ShowSafetySetup()
    {
        if (Context is null) return;

        SafetySetupLaunch.Run(
            windowOpen: _settingsWindow is not null,
            openWindow: OpenSettingsWindow,
            start: closeWhenDone => _settingsWindow?.StartSafetySetup(closeWhenDone));
    }

    /// <summary>Constructs and shows a fresh settings window. Never navigates; the caller does.</summary>
    private void OpenSettingsWindow()
    {
        var context = Context!;

        _modal.SettingsOpen = true;
        _modal.SettingsOpenChanged?.Invoke(true);
        _toolbar.SetAutoHideSuspended("settings", true);
        _quickActions.SetSummonSuspended("settings", true);
        context.OverlayManager.CursorCircle.Hide();

        try
        {
            // Create SettingsViewModels
            var settingsVM = new SettingsViewModel(context.Settings);

            var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
                context.QuickActionsStore, context.InstalledAppProvider);
            quickActionsEditor.Load();

            var settingsWindowVM = new SettingsWindowViewModel(
                settingsVM,
                quickActionsEditor,
                context.Messenger,
                context.Settings.Dwell);

            _settingsWindow = new SettingsWindow(
                settingsWindowVM,
                context.Settings,
                context.SettingsService,
                _modelRegistry ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _modelManagementService ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                context.Messenger,
                context.CalibrationService,
                _restartCoordinator ?? throw new InvalidOperationException(
                    "Restart coordinator not set; SetRestartCoordinator must be called at startup."),
                _supervisionToggle ?? throw new InvalidOperationException(
                    "Supervision toggle not set; SetSupervisionToggle must be called at startup."),
                context.UserDictionary,
                probeBlockReason: _probeBlockReason,
                requestProbe: _probeRequest,
                canResumeSession2: _probeCanResume,
                setupGateways: _setupGatewaysFactory?.Invoke(),
                uninstallHandoff: UninstallHandoff);

            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
            _handoffWindow = new SettingsWindowHandle(_settingsWindow);
        }
        catch
        {
            _modal.SettingsOpen = false;
            _modal.SettingsOpenChanged?.Invoke(false);
            // Window construction threw before Show() — OnSettingsWindowClosed will never fire, so
            // undo the suspend we set alongside _modal.SettingsOpen = true above.
            _toolbar.SetAutoHideSuspended("settings", false);
            _quickActions.SetSummonSuspended("settings", false);
            if (context.CursorVM.IsVisible)
                context.OverlayManager.CursorCircle.Show();
            throw;
        }
    }

    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (Context is null) return;

        Context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        Context.InteractionManager.SelectionMode = s.SelectionMode;
        Context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        Context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot != toolbarSnapshot)
        {
            ReinitializeToolbar(s);
        }

        // The keyboard snapshot compare + reload lives on KeyboardCoordinator (cluster 3), exposed
        // as the ReloadIfChanged seam. When the snapshot did NOT change (live-read settings like
        // MaxSuggestions, ShowPredictionConfidence, ShowPredictionTypes), the keyboard still
        // displays cells built from the OLD values — and the keyboard stays visible and dwellable
        // under the settings window (gaze feeds it on KeyboardVisible alone), so a lowered
        // suggestion count left stale cells whose completed dwell OnPredictionDwelled silently
        // swallowed (index guard, no redraw). Refresh the bar instead: idempotent, and it
        // retires the unreachable targets the moment the setting moves.
        if (ShouldRefreshPredictionBar(
                _keyboard.ReloadIfChanged(s, Context.InputSimulator, Context.SettingsService),
                _modal.KeyboardVisible))
        {
            Context.OverlayManager.Keyboard.UpdatePredictions();
        }

        Log.Information("Settings saved");
    }

    /// <summary>
    /// Rebuilds the toolbar band at the settings' metrics, with <c>Adaptation.Enabled</c> as the
    /// includeReset flag (as at startup, Task 7 / D5/M5 — Reset is on the band only while
    /// adaptation is on), then re-applies the dock mode, re-evaluates auto-hide enablement
    /// (IsDocked may have flipped to false → disable + force-show), refreshes the cached edge,
    /// repositions the pause indicator along it, and re-captures the auto-hide rect.
    /// <para>
    /// Two callers, one guard: the save path (the toolbar-metrics snapshot branch of
    /// <see cref="OnSettingsChanged"/>) and the <see cref="AdaptationSettings.Enabled"/>
    /// subscription — the settings UI mutates the live object as the user toggles, so a flip
    /// that lands in the same save as a metrics change would otherwise rebuild the band twice.
    /// The last applied (metrics snapshot, includeReset) pair is remembered and an identical
    /// request returns, making the rebuild exactly-once in either arrival order.
    /// </para>
    /// </summary>
    private void ReinitializeToolbar(EyeCtrlSettings s)
    {
        if (Context is null) return;

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot == toolbarSnapshot && _appliedIncludeReset == s.Adaptation.Enabled)
            return;

        Context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs,
            s.Adaptation.Enabled);
        Context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);

        bool nowEnabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.Toolbar.IsDocked;
        _toolbar.UpdateEnabled(nowEnabled);
        _toolbar.UpdateEdge(s.Toolbar.DockedEdge);
        _toolbar.PositionIndicator(s.Toolbar.DockedEdge);
        _toolbar.RefreshToolbarRect();

        _toolbarSnapshot = toolbarSnapshot;
        _appliedIncludeReset = s.Adaptation.Enabled;
    }

    /// <summary>
    /// The prediction-bar refresh gate, extracted so it is unit-testable headlessly: review round 2
    /// deleted the entire inline block and no test failed — the same zero-coverage discovery that
    /// produced <c>ShouldFeedKeyboardSample</c> one file over. A reload already rebuilt the bar, so
    /// refresh only when it did NOT run and the keyboard can be showing cells at all.
    /// </summary>
    internal static bool ShouldRefreshPredictionBar(bool reloaded, bool keyboardVisible) =>
        !reloaded && keyboardVisible;

    /// <summary>Settings' close path, `internal` so the W2/W3 probe tests can drive the real
    /// handler (review 03 F1: it is otherwise reachable only through a real SettingsWindow).</summary>
    internal void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (Context is null) return;

        _settingsWindow = null;
        // fixup-08: the window is gone, so there is no hidden window to bring back — a running
        // uninstall watch must find nothing to show rather than a stale handle.
        _handoffWindow = null;
        _settingsHiddenForUninstallHandoff = false;
        _modal.SettingsOpen = false;
        _modal.SettingsOpenChanged?.Invoke(false);
        _toolbar.SetAutoHideSuspended("settings", false);
        // B7 fixup-05 L-b: release Settings' own summon claim UNCONDITIONALLY. Under named claims
        // the probe holds (and releases) its own "probe" claim, so guarding this on ProbeOpen
        // could leak "settings" until Settings is opened and closed again. (Before claims, this
        // guard existed because the probe's Restore resumed the shared bool for everyone.)
        _quickActions.SetSummonSuspended("settings", false);
        if (Context.CursorVM.IsVisible && !_modal.ProbeOpen)
            Context.OverlayManager.CursorCircle.Show();
    }

    // ── C9 S6e fixup-08: the uninstall hand-off's hide / come-back ────────────────────────────
    //
    // Owner decision 06.11: Settings hides when Setup's uninstall really starts and comes back by
    // itself on every outcome that does not remove EyeCtrl. The reason is z-order, not taste:
    // since #316 Settings sits in ZBID_UIACCESS (band 2) while Setup runs at uiAccess=false, so
    // Setup's topmost Q8 can never be raised above it. Hiding is the only thing that works.

    /// <summary>
    /// The presenter the „Erweitert" page's uninstall row starts. Built once, lazily; every
    /// hand-off creates a fresh one-shot watch.
    /// </summary>
    private IUninstallHandoffPresenter UninstallHandoff =>
        _uninstallHandoff ??= new UninstallHandoffPresenter(
            HideSettingsForUninstallHandoff,
            RestoreSettingsAfterUninstallHandoff,
            UninstallHandoffWatchFactory.CreateForProduction(),
            warn: line => Log.Warning("{UninstallHandoff}", line));

    /// <summary>
    /// Hides the open Settings window for the hand-off, releasing the flags it holds while open —
    /// the toolbar's auto-hide suspension, the quick-actions summon claim and the ring's hide — so
    /// the owner keeps a usable toolbar and ring next to Setup's window. Returns whether a visible
    /// window was hidden; false means there is nothing to bring back.
    /// </summary>
    internal bool HideSettingsForUninstallHandoff()
    {
        if (Context is null) return false;
        if (_settingsHiddenForUninstallHandoff) return false;
        if (_handoffWindow is not { IsVisible: true } window) return false;

        window.Hide();
        _settingsHiddenForUninstallHandoff = true;

        _modal.SettingsOpen = false;
        _modal.SettingsOpenChanged?.Invoke(false);
        _toolbar.SetAutoHideSuspended("settings", false);
        _quickActions.SetSummonSuspended("settings", false);
        if (Context.CursorVM.IsVisible && !_modal.ProbeOpen)
            Context.OverlayManager.CursorCircle.Show();

        Log.Information("Settings hidden for the uninstall hand-off");
        return true;
    }

    /// <summary>
    /// The watch's end. Only <see cref="UninstallHandoffEnd.Uninstalled" /> keeps Settings hidden:
    /// Setup stops the App in the same run, and a window raised over the removal would be the last
    /// thing the owner sees. Everything else — cancel, „Nein, behalten", Esc, close, the 5-minute
    /// timeout, a refusal, a failure, a watcher that never learned the outcome — brings it back.
    /// A restore that arrives after the owner already reopened Settings (or after the window was
    /// closed) does nothing.
    /// </summary>
    internal void RestoreSettingsAfterUninstallHandoff(
        UninstallHandoffEnd end, SetupOutcome? outcome = null)
    {
        if (Context is null) return;

        if (end == UninstallHandoffEnd.Uninstalled)
        {
            Log.Information("Settings stays hidden: the uninstall completed");
            return;
        }

        if (!_settingsHiddenForUninstallHandoff)
        {
            // The owner already reopened it: nothing to show, but the end still belongs in the log.
            Log.Information(
                "Uninstall hand-off ended after the owner reopened Settings: {End} ({Outcome})",
                end, outcome);
            return;
        }

        BringSettingsBackAfterUninstallHandoff(reopenedByOwner: false);
        Log.Information(
            "Settings restored after the uninstall hand-off: {End} ({Outcome})", end, outcome);
    }

    /// <summary>
    /// The owner's manual reopen while the uninstall hand-off hid the window: show it again on
    /// „Erweitert" and cancel the automatic return. Returns whether a hidden window was brought
    /// back. Internal so the headless contract can prove the one-shot property and that the later
    /// watch end does not show it a second time.
    /// </summary>
    internal bool ReopenHiddenSettingsWindowForUninstallHandoff()
    {
        if (!_settingsHiddenForUninstallHandoff)
            return false;

        BringSettingsBackAfterUninstallHandoff(reopenedByOwner: true);
        return true;
    }

    /// <summary>
    /// Shows the hidden window again on „Erweitert" and re-takes the flags Settings holds while
    /// open. One path for the automatic return and the owner's manual reopen, so the two can never
    /// diverge; the second caller finds the flag already cleared and is a no-op.
    /// </summary>
    private void BringSettingsBackAfterUninstallHandoff(bool reopenedByOwner)
    {
        if (!_settingsHiddenForUninstallHandoff) return;

        var window = _handoffWindow;

        if (window is null)
        {
            // The window was closed while hidden: there is nothing to show, and the close path
            // has already released every flag.
            _settingsHiddenForUninstallHandoff = false;
            return;
        }

        _modal.SettingsOpen = true;
        _modal.SettingsOpenChanged?.Invoke(true);
        _toolbar.SetAutoHideSuspended("settings", true);
        _quickActions.SetSummonSuspended("settings", true);
        Context!.OverlayManager.CursorCircle.Hide();

        try
        {
            window.ShowOnAdvanced();
            _settingsHiddenForUninstallHandoff = false;
        }
        catch (Exception ex)
        {
            // Keep the flag so a later Settings request retries the show instead of reporting a
            // window that is still hidden as open.
            Log.Warning(ex, "Could not show the Settings window after the uninstall hand-off");
            return;
        }

        if (reopenedByOwner)
            Log.Information("Settings reopened by the owner during the uninstall hand-off");
    }

    /// <summary>
    /// Test seam: sets the window handle the hide/restore drives. Production assigns it once per
    /// window in <see cref="OpenSettingsWindow" />; a test supplies a recorder, because a real
    /// Settings window may never be shown from a test.
    /// </summary>
    internal void SetHandoffWindowForTests(ISettingsWindowHandle? window) =>
        _handoffWindow = window;

    protected override void OnDispose()
    {
        // Close the settings window before tearing down the rest of EC (EC.Dispose calls this
        // FIRST). Unsubscribe the Closed handler before Close() so the synchronous Closed event
        // doesn't re-enter OnSettingsWindowClosed (which would touch the toolbar/quick-actions
        // suspend state and null _settingsWindow mid-teardown).
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.SettingsChanged -= OnSettingsChanged;
            // Suppress a throw during shutdown so it can't cascade out of EventCoordinator.Dispose
            // and skip the user-dictionary save + other App.OnExit saves.
            try { _settingsWindow.Close(); }
            catch (Exception ex) { Log.Warning(ex, "Suppressed error closing settings window during shutdown"); }
            _settingsWindow = null;
            _handoffWindow = null;
            _settingsHiddenForUninstallHandoff = false;
        }

        if (Context is not null)
        {
            Context.Settings.TrackStatus.PropertyChanged -= OnTrackStatusSettingsPropertyChanged;
            Context.Settings.Adaptation.PropertyChanged -= OnAdaptationSettingsPropertyChanged;
        }
    }
}
