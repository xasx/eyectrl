namespace EyeCtrl.App.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the toolbar lifecycle surface of <see cref="EventCoordinator"/>: the auto-hide controller,
/// the visibility adapter, toolbar-rect capture, the StateChangedMessage → auto-hide-pause mirror,
/// and the per-sample gaze route (toolbar lazy-init + keyboard/settings forwarding). Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (clusters 6 + 8).
/// </summary>
internal sealed class ToolbarLifecycleCoordinator : CoordinatorBase
{
    // Forward-looking seam: the modal flags are injected here so T5/T7 (keyboard/settings) can take
    // the same shared ModalInteractionState and flip it without re-plumbing the constructor.
    private readonly ModalInteractionState _modal;
    private ToolbarAutoHideController? _autoHide;
    private ToolbarVisibilityAdapter? _visibility;
    // P8 (Task 10): the named holders whose suspensions are currently live. Empty ⇒ auto-hide runs;
    // non-empty ⇒ suspended. A HashSet rather than a counter so an accidental double-release of one
    // holder cannot resume under another still-open holder (a counter's -1 would).
    private readonly HashSet<string> _autoHideSuspensionHolders = new(StringComparer.Ordinal);
    private Action<GazePoint>? _autoHideGazeHandler; // held so Dispose can unsubscribe (the lambda captures the controller local, not this field)
    // Per-sample CursorMoved route: toolbar (lazy first sample), keyboard (while open), settings
    // window (while open). Moved here from EventCoordinator.SetupGazeRouting as part of the cluster-8
    // final cleanup. Held so Dispose can -= it.
    private Action<GazeFrame>? _gazeMovedHandler;
    // True once the toolbar has received its first gaze sample and lazily initialized its target
    // positions. The per-sample early-out below keys off this.
    private volatile bool _toolbarInitialized;
    // SettingsUiCoordinator is constructed AFTER this coordinator in Initialize task order, so it
    // cannot be a ctor param. EC calls AttachSettingsUi immediately after constructing it, before
    // the first gaze sample. Private: AttachSettingsUi is the only writer, SetupGazeRouting the only
    // reader — nothing outside this class touches the field.
    private SettingsUiCoordinator? _settingsUi;
    public ToolbarLifecycleCoordinator(ModalInteractionState modal) =>
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));

    /// <summary>Re-exposes the visibility adapter's edge-detected transition event (spec B3 #5),
    /// so KeyboardCoordinator can yield the keyboard without referencing the adapter. Fires on
    /// the UI thread.</summary>
    public event Action<bool>? ToolbarVisibilityChanged;

    // Test seams (production never calls these): inject a pre-built controller so the suspend/resume
    // path can be exercised without the SystemParameters/DpiHelper/live-window creation path in
    // SetupToolbarAutoHide, and read back the suspended state without reflection. Mirrors
    // QuickActionsCoordinator.InjectSummonForTesting / IsSummonSuspended.
    internal void InjectAutoHideForTesting(ToolbarAutoHideController autoHide) => _autoHide = autoHide;
    internal bool IsAutoHideSuspended => _autoHide?.IsSuspended ?? true;

    /// <summary>Wires the settings-UI sub-coordinator so the per-sample gaze route can forward to
    /// its open window. Called once from EventCoordinator.Initialize after SettingsUiCoordinator is
    /// constructed (which happens after this coordinator).</summary>
    internal void AttachSettingsUi(SettingsUiCoordinator settings) => _settingsUi = settings;

    protected override void OnInitialize()
    {
        var context = Context!;
        // Mirror InteractionManager.Paused into the toolbar auto-hide controller. This registration
        // moved here from EventCoordinator.SetupMessageHandlers as part of the cluster-6 extraction.
        context.Messenger.Register<ToolbarLifecycleCoordinator, StateChangedMessage>(this, (_, msg) =>
        {
            var paused = msg.NewState == InteractionState.Paused;
            _autoHide?.SetPaused(paused);
            // The strip is a pure function of paused state (spec H1/N2): one command per flip.
            // The startup-paused push arrives here from App.OnStartup's TogglePause, which runs
            // AFTER ShowOverlays — that ordering is load-bearing for the strip's z-order (O4);
            // see the comment at App.xaml.cs.
            _visibility?.SetPauseIndicator(paused);
        });

        // The screen changed size or scale. Both of this controller's geometry values were captured
        // once at startup and could never be updated — the summon region, which is the escape
        // hatch, was being measured against a screen that no longer existed.
        context.Messenger.Register<ToolbarLifecycleCoordinator, DisplayGeometryChangedMessage>(this, (_, msg) =>
            RefreshGeometry(msg.Geometry));

        SetupToolbarAutoHide();
        SetupGazeRouting();
    }

    /// <summary>Subscribes the per-sample CursorMoved route. Targets: toolbar (lazy first-sample
    /// init; afterwards its dwell is driven by GazeCursorMovedMessage on the messenger), keyboard
    /// (while open), and the settings window (while open, via the SettingsUiCoordinator seam).
    /// Moved here from EventCoordinator as part of the cluster-8 final cleanup.</summary>
    private void SetupGazeRouting()
    {
        if (Context is null) return;

        var toolbar = Context.OverlayManager.Toolbar;
        var keyboard = Context.OverlayManager.Keyboard;

        _gazeMovedHandler = frame =>
        {
            var point = frame.Position;
            var timestamp = frame.TimestampMicroseconds;

            // Perf: skip the per-sample UI-thread dispatch when nothing consumes it. The toolbar
            // needs samples only until its target positions are placed (which can take more than
            // one, since the band's faces may not be arranged yet); after that its dwell is driven
            // by GazeCursorMovedMessage on the messenger, not this route. The
            // keyboard and settings window need gaze only while open. Flags are volatile and read
            // on the gaze thread; a one-frame-stale read at worst delays a dispatch by a frame.
            if (_toolbarInitialized && !_modal.KeyboardVisible && !_modal.SettingsOpen)
                return;

            // Pause gates the KEYBOARD feed only (the toolbar deliberately keeps receiving samples
            // so gaze can unpause). The engine stops moving the cursor and detecting fixations
            // while paused, but the keyboard's own dwell used to keep accumulating on the frozen
            // ring position — typing into whatever app held focus while the user believed input
            // was fully released. Read per sample on the gaze thread, like the modal flags above;
            // a one-frame-stale read at worst types one key past the pause.
            var keyboardGated = Context.GazeEngine.InteractionManager.CurrentState == InteractionState.Paused;

            Context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);
                // Latch on placement having SUCCEEDED, not on one delivery having happened. The
                // old unconditional `= true` gave the toolbar a single attempt: if the band's
                // faces had not been arranged yet (App.ShowOverlays rebuilds every button via
                // BindToViewModel immediately before gazeEngine.Start()), that attempt placed
                // nothing and no sample was ever delivered again — a visible toolbar on which no
                // button could be dwelled, unrecoverable short of a restart. Reading the window's
                // own flag also makes SetMode's `_targetsInitialized = false` mean something:
                // a re-dock now gets its targets moved instead of being silently ignored.
                _toolbarInitialized = toolbar.TargetsInitialized;

                // The keyboard is a dwell surface (key dwell, prediction dwell, emoji categories), so a
                // gap-held sample is withheld from it: a held position carries no evidence of where the
                // user is looking, and feeding it would keep dwell progress alive over a blink. That is
                // a display/semantics guard, NOT the anti-blink bound. Withholding is not a pause — the
                // tracker is timestamp-based, and the first real sample after a gap used to be credited
                // the whole gap. The actual bound is the credited-elapsed clamp in
                // KeyboardViewModel.ProcessKeyGaze (MaxContinuousSampleGapUs). Do not remove the clamp on
                // this gate's authority: this comment once claimed the gate stopped blink typing, and F3
                // is what that false claim cost. Do not remove the GATE on the clamp's authority either:
                // held samples fed at tracker cadence stay under MaxContinuousSampleGapUs and would
                // re-credit a blink as dwell — the gate creates the silence, the clamp interprets it, and
                // each looks removable while the other holds. The toolbar and settings routes above are
                // geometry-only
                // (target placement) and are fed regardless. Paused samples are skipped for the same
                // reason: input must not leave EyeCtrl while the user believes pause released it.
                if (ShouldFeedKeyboardSample(keyboardGated, _modal.KeyboardVisible, keyboard.IsVisible, frame.IsInGap, _modal.ProbeOpen))
                    keyboard.UpdateGaze(point, timestamp);

                // Route the sample to the settings window iff it is open (the visibility check lives
                // inside UpdateSettingsGazeIfVisible, which is a no-op when no window is constructed).
                _settingsUi?.UpdateSettingsGazeIfVisible(point, timestamp);
            });
        };

        Context.GazeEngine.CursorMoved += _gazeMovedHandler;
    }

    /// <summary>
    /// The keyboard-feed gate, extracted so it is unit-testable headlessly: the keyboard is OS text
    /// injection, so it must be fed only when unpaused AND the modal state says the keyboard is open
    /// AND the window is visible AND the sample is a real measurement (not gap-held) AND the probe
    /// does not own the screen (build plan C9). Review found the inline version had zero test
    /// coverage — neutralizing it broke no test.
    /// </summary>
    internal static bool ShouldFeedKeyboardSample(bool paused, bool modalKeyboardVisible, bool keyboardIsVisible, bool isInGap, bool probeOpen) =>
        !paused && modalKeyboardVisible && keyboardIsVisible && !isInGap && !probeOpen;

    /// <summary>
    /// The auto-hide controller's gaze gate (build plan C9): while the probe is open no auto-hide or
    /// summon sample may act — the probe window covers the toolbar, so an off-toolbar "hide" would
    /// blank a band the user cannot see, and a past-edge summon would re-arm it under the probe.
    /// Pure-static, like <see cref="ShouldFeedKeyboardSample"/>, so W4 drives the REAL controller
    /// with literals and never touches <c>Context</c> — which is the review 03 N5a concern; there is
    /// no short-circuit, both booleans are evaluated eagerly at the construction site. The wiring
    /// that passes <c>_modal.ProbeOpen</c> there is pinned at source in
    /// ToolbarLifecycleCoordinatorTests.ProbeGateWiring_IsPinnedAtTheSource.
    /// </summary>
    internal static bool AutoHideGateClosed(bool probeOpen, bool calibrationActive) =>
        probeOpen || calibrationActive;

    /// <summary>Suspend/resume the auto-hide dwell (called by Keyboard/Settings coordinators when a
    /// modal opens, and by the adaptation preview while its panel is up). Wraps
    /// <see cref="ToolbarAutoHideController.SetSuspended"/>.
    /// <para>
    /// Named holders (Task 10, P8): the old single-bool API let a later release clobber an earlier,
    /// still-live suspension — the preview releasing while the settings window was open resumed
    /// auto-hide under the modal. Each caller holds and releases only its own name
    /// (<c>"settings"</c>, <c>"adaptation-preview"</c>); the controller sees suspended iff the
    /// holder set is non-empty.
    /// </para>
    /// </summary>
    public void SetAutoHideSuspended(string holder, bool suspended)
    {
        ArgumentException.ThrowIfNullOrEmpty(holder);
        if (suspended)
            _autoHideSuspensionHolders.Add(holder);
        else
            _autoHideSuspensionHolders.Remove(holder);
        _autoHide?.SetSuspended(_autoHideSuspensionHolders.Count > 0);
    }

    /// <summary>Activates/deactivates the toolbar visibility adapter (calibration suppress path).
    /// Wraps <see cref="ToolbarVisibilityAdapter.SetActive"/>.</summary>
    public void SetActive(bool active) => _visibility?.SetActive(active);

    /// <summary>Tray fallback: force-show the toolbar regardless of auto-hide state. Flips the
    /// visibility adapter AND resets the controller to Visible so it doesn't immediately re-hide.</summary>
    public void ForceShow()
    {
        _visibility?.ForceShow();
        // Also flip the controller into Visible so it doesn't immediately re-hide.
        _autoHide?.ResetToVisible();
    }

    public void UpdateEnabled(bool enabled) => _autoHide?.UpdateEnabled(enabled);
    public void UpdateEdge(string edge) => _autoHide?.UpdateEdge(edge);
    public void ReassertVisibility() => _autoHide?.ReassertVisibility();
    public void PositionIndicator(string edge) => _visibility?.PositionIndicator(edge);

    /// <summary>Public no-arg seam: re-captures the toolbar rect at Loaded priority. The
    /// <see cref="System.Windows.Threading.DispatcherPriority.Loaded"/> deferral and the
    /// <see cref="CaptureToolbarRect"/> call live INSIDE this method — callers must not rebuild a
    /// <see cref="PhysicalRect"/> themselves.</summary>
    public void RefreshToolbarRect()
    {
        if (Context is null || _autoHide == null) return;
        Context.Dispatcher.BeginInvoke(new Action(() =>
        {
            var rect = CaptureToolbarRect();
            Log.Information("Toolbar auto-hide rect refreshed: {Rect}", rect);
            _autoHide.UpdateToolbarRect(rect);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-publishes the screen bounds and the summon rectangle together after a display change.
    /// </summary>
    /// <remarks>
    /// One <see cref="ToolbarAutoHideController.UpdateGeometry"/> call rather than a bounds setter
    /// plus <see cref="RefreshToolbarRect"/>: a rect measured on the new screen but judged against
    /// the old bounds (or the reverse) can leave the toolbar permanently unsummonable, which is a
    /// subtler version of the bug this whole change fixes.
    /// <para>
    /// Deferred to <c>Loaded</c> priority for the same reason <see cref="RefreshToolbarRect"/> is:
    /// WPF must re-layout for the new screen before the window can be measured, and measuring
    /// immediately would capture the pre-change layout. The cost is that the toolbar's geometry
    /// lands one dispatcher cycle after the gaze provider's, so a handful of samples may be judged
    /// against the previous rect. Bounded and self-correcting — unlike measuring the wrong layout,
    /// which would persist.
    /// </para>
    /// <para>
    /// <b>Not done here:</b> physically repositioning the docked toolbar (and the other overlays)
    /// for the new screen. This restores the coordinate agreement gaze and dwell depend on; moving
    /// the windows is a separate piece of work across ~20 overlays.
    /// </para>
    /// </remarks>
    private void RefreshGeometry(DisplayGeometry geometry)
    {
        if (Context is null || _autoHide == null) return;
        Context.Dispatcher.BeginInvoke(new Action(() =>
        {
            _autoHide.UpdateGeometry(geometry.WidthPx, geometry.HeightPx, CaptureToolbarRect());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private PhysicalRect CaptureToolbarRect()
    {
        var t = Context!.OverlayManager.Toolbar;
        // Convert logical window bounds (WPF DIPs) to physical px.
        // The window converts its own layout now — see IToolbarOverlay.GetPhysicalBounds. Reading
        // Left/Top/ActualWidth/ActualHeight here was the only thing forcing this coordinator to
        // depend on the concrete WPF window.
        return t.GetPhysicalBounds();
    }

    private void SetupToolbarAutoHide()
    {
        if (Context is null) return;

        var s = Context.Settings.Toolbar;
        // Two distinct gates:
        //  - providerIsRealTracker (immutable for the process): whether the subscription is wired at all.
        //    Mouse/test mode never subscribes → zero overhead, no auto-hide ever.
        //  - enabled (mutable via UpdateEnabled when IsDocked is toggled at runtime): the per-sample
        //    Feed() guard (Feed early-returns when !_enabled). Subscribe under any real tracker even
        //    if the toolbar starts FLOATING — otherwise a runtime dock-toggle couldn't re-enable
        //    auto-hide (the subscription would never exist). Feed's guard makes an unsubscribed
        //    floating toolbar a cheap no-op.
        bool providerIsRealTracker = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool enabled = providerIsRealTracker && s.IsDocked;

        // Cache the toolbar rect in physical px from the live window.
        var toolbarRect = CaptureToolbarRect();

        var controller = new ToolbarAutoHideController(
            enabled: enabled,
            edge: s.DockedEdge,
            screenW: System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            screenH: System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            toolbarRect: toolbarRect,
            hideDelayMs: 5000,
            summonDelayMs: 400,
            offScreenMarginPx: 50,
            calibrationActive: () => AutoHideGateClosed(_modal.ProbeOpen, Context.CalibrationService.IsSessionActive),
            // Read per call, not cached, so a mid-session recalibration takes effect. Identity while
            // no calibration is loaded, which makes this a no-op until one is. Deliberately NOT
            // passed to QuickActionsSummonController below: its IsPastEdge test is purely off-screen,
            // where the transform extrapolates outside the domain it was fit to.
            toScreen: p => Context.CalibrationService.CurrentTransform?.Apply(p) ?? p);

        var vis = new ToolbarVisibilityAdapter(Context.OverlayManager, Context.Dispatcher, Context.ToolbarVM);
        controller.Attach(vis);
        // The summon-failure diagnosis of 2026-09-26 hinged on the summon run's start/reset being
        // silent; wire the controller's transition lines into the same log so one real run is
        // decisive. See ToolbarAutoHideController._diagnostics.
        controller.AttachDiagnostics(message => Log.Information("Toolbar auto-hide: {Detail}", message));
        vis.VisibilityChanged += visible => ToolbarVisibilityChanged?.Invoke(visible);

        Log.Information(
            "Toolbar auto-hide wired: edge={Edge} enabled={Enabled} rect={Rect} screen={W}x{H} hideMs=5000 summonMs=400 margin=50",
            s.DockedEdge, enabled, toolbarRect,
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);

        _autoHide = controller;
        _visibility = vis;

        // Subscribe under any real tracker (NOT gated on `enabled`/IsDocked) so a runtime dock-toggle
        // can activate auto-hide. Feed() early-returns when !_enabled, so samples are a cheap no-op
        // while the toolbar is floating or the feature otherwise disabled.
        if (providerIsRealTracker)
        {
            // Store the handler in a field so Dispose can -= it. (The lambda captures the `controller`
            // local; nulling _autoHide alone would NOT unsubscribe it.)
            _autoHideGazeHandler = sample => controller.Feed(sample, sample.TimestampMicroseconds);
            Context.GazeEngine.GazeSampled += _autoHideGazeHandler;
        }

        // CaptureToolbarRect() above ran during Initialize, BEFORE ShowOverlays shows + lays out the
        // toolbar (App.OnStartup order: Initialize → ShowOverlays → gazeEngine.Start). At that point
        // ActualWidth/ActualHeight are 0 (the toolbar is SizeToContent and unmeasured). Enqueue a
        // deferred re-capture at Loaded priority so the controller's hit-test rect is correct by the
        // time the first gaze sample arrives. (Spec Edge case #7: wait one layout pass before caching.)
        RefreshToolbarRect();
    }

    protected override void OnDispose()
    {
        if (_autoHideGazeHandler != null) Context!.GazeEngine.GazeSampled -= _autoHideGazeHandler;
        if (_gazeMovedHandler != null) Context!.GazeEngine.CursorMoved -= _gazeMovedHandler;
    }
}
