namespace EyeCtrl.App.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the toolbar lifecycle surface of <see cref="EventCoordinator"/>: the auto-hide controller,
/// the visibility adapter, toolbar-rect capture, the StateChangedMessage → auto-hide-pause mirror,
/// and the per-sample gaze route (toolbar lazy-init + keyboard/settings forwarding). Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (clusters 6 + 8).
/// </summary>
internal sealed class ToolbarLifecycleCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    // Forward-looking seam: the modal flags are injected here so T5/T7 (keyboard/settings) can take
    // the same shared ModalInteractionState and flip it without re-plumbing the constructor.
    private readonly ModalInteractionState _modal;
    private ToolbarAutoHideController? _autoHide;
    private ToolbarVisibilityAdapter? _visibility;
    private Action<GazePoint>? _autoHideGazeHandler; // held so Dispose can unsubscribe (the lambda captures the controller local, not this field)
    // Per-sample CursorMoved route: toolbar (lazy first sample), keyboard (while open), settings
    // window (while open). Moved here from EventCoordinator.SetupGazeRouting as part of the cluster-8
    // final cleanup. Held so Dispose can -= it.
    private Action<PhysicalPoint, long>? _gazeMovedHandler;
    // True once the toolbar has received its first gaze sample and lazily initialized its target
    // positions. The per-sample early-out below keys off this.
    private volatile bool _toolbarInitialized;
    // SettingsUiCoordinator is constructed AFTER this coordinator in Initialize task order, so it
    // cannot be a ctor param. EC calls AttachSettingsUi immediately after constructing it, before
    // the first gaze sample.
    internal SettingsUiCoordinator? _settingsUi;
    private bool _disposed;

    public ToolbarLifecycleCoordinator(ModalInteractionState modal) => _modal = modal;

    /// <summary>Wires the settings-UI sub-coordinator so the per-sample gaze route can forward to
    /// its open window. Called once from EventCoordinator.Initialize after SettingsUiCoordinator is
    /// constructed (which happens after this coordinator).</summary>
    internal void AttachSettingsUi(SettingsUiCoordinator settings) => _settingsUi = settings;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"

        // Mirror InteractionManager.Paused into the toolbar auto-hide controller. This registration
        // moved here from EventCoordinator.SetupMessageHandlers as part of the cluster-6 extraction.
        context.Messenger.Register<ToolbarLifecycleCoordinator, StateChangedMessage>(this, (_, msg) =>
            _autoHide?.SetPaused(msg.NewState == InteractionState.Paused));

        SetupToolbarAutoHide();
        SetupGazeRouting();
    }

    /// <summary>Subscribes the per-sample CursorMoved route. Targets: toolbar (lazy first-sample
    /// init; afterwards its dwell is driven by GazeCursorMovedMessage on the messenger), keyboard
    /// (while open), and the settings window (while open, via the SettingsUiCoordinator seam).
    /// Moved here from EventCoordinator as part of the cluster-8 final cleanup.</summary>
    private void SetupGazeRouting()
    {
        if (_context == null) return;

        var toolbar = _context.OverlayManager.Toolbar;
        var keyboard = _context.OverlayManager.Keyboard;

        _gazeMovedHandler = (point, timestamp) =>
        {
            // Perf: skip the per-sample UI-thread dispatch when nothing consumes it. The toolbar
            // only needs the first sample (to lazily init its target positions); after that its
            // dwell is driven by GazeCursorMovedMessage on the messenger, not this route. The
            // keyboard and settings window need gaze only while open. Flags are volatile and read
            // on the gaze thread; a one-frame-stale read at worst delays a dispatch by a frame.
            if (_toolbarInitialized && !_modal.KeyboardVisible && !_modal.SettingsOpen)
                return;

            _context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);
                _toolbarInitialized = true;

                if (_modal.KeyboardVisible && keyboard.IsVisible)
                    keyboard.UpdateGaze(point, timestamp);

                // Route the sample to the settings window iff it is open (the visibility check lives
                // inside UpdateSettingsGazeIfVisible, which is a no-op when no window is constructed).
                _settingsUi?.UpdateSettingsGazeIfVisible(point, timestamp);
            });
        };

        _context.GazeEngine.CursorMoved += _gazeMovedHandler;
    }

    /// <summary>Suspend/resume the auto-hide dwell (called by Keyboard/Settings coordinators when a
    /// modal opens). Wraps <see cref="ToolbarAutoHideController.SetSuspended"/>.</summary>
    public void SetAutoHideSuspended(bool suspended) => _autoHide?.SetSuspended(suspended);

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
        if (_context == null || _autoHide == null) return;
        _context.Dispatcher.BeginInvoke(new Action(() =>
        {
            _autoHide.UpdateToolbarRect(CaptureToolbarRect());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private PhysicalRect CaptureToolbarRect()
    {
        var t = _context!.OverlayManager.Toolbar;
        // Convert logical window bounds (WPF DIPs) to physical px.
        double x = DpiHelper.LogicalToPhysicalX(t.Left);
        double y = DpiHelper.LogicalToPhysicalY(t.Top);
        double w = DpiHelper.LogicalToPhysicalX(t.ActualWidth);
        double h = DpiHelper.LogicalToPhysicalY(t.ActualHeight);
        return new PhysicalRect(x, y, w, h);
    }

    private void SetupToolbarAutoHide()
    {
        if (_context == null) return;

        var s = _context.Settings.Toolbar;
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
            calibrationActive: () => _context.CalibrationService.IsSessionActive,
            // Read per call, not cached, so a mid-session recalibration takes effect. Identity while
            // no calibration is loaded, which makes this a no-op until one is. Deliberately NOT
            // passed to QuickActionsSummonController below: its IsPastEdge test is purely off-screen,
            // where the transform extrapolates outside the domain it was fit to.
            toScreen: p => _context.CalibrationService.CurrentTransform?.Apply(p) ?? p);

        var vis = new ToolbarVisibilityAdapter(_context.OverlayManager, _context.Dispatcher, _context.ToolbarVM);
        controller.Attach(vis);

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
            _context.GazeEngine.GazeSampled += _autoHideGazeHandler;
        }

        // CaptureToolbarRect() above ran during Initialize, BEFORE ShowOverlays shows + lays out the
        // toolbar (App.OnStartup order: Initialize → ShowOverlays → gazeEngine.Start). At that point
        // ActualWidth/ActualHeight are 0 (the toolbar is SizeToContent and unmeasured). Enqueue a
        // deferred re-capture at Loaded priority so the controller's hit-test rect is correct by the
        // time the first gaze sample arrives. (Spec Edge case #7: wait one layout pass before caching.)
        RefreshToolbarRect();
    }

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        if (_autoHideGazeHandler != null) _context.GazeEngine.GazeSampled -= _autoHideGazeHandler;
        if (_gazeMovedHandler != null) _context.GazeEngine.CursorMoved -= _gazeMovedHandler;
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
