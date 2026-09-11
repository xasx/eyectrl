namespace EyeCtrl.App.Services;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Manages ManualSelection dwell detection and gaze indicator updates during ManualSelection mode.
/// Subscribes to GazeCursorMovedMessage and emits ManualSelectionDwellCompletedMessage.
/// Extracts the 130+ lines of ManualSelection dwell logic from App.xaml.cs into a testable service.
/// </summary>
public class ManualSelectionDwellService : IDisposable
{
    private const string ManualSelectionDwellTargetId = "ManualSelection_dwell_target";

    private readonly IMessenger _messenger;
    private readonly ILogger<ManualSelectionDwellService> _logger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;
    private readonly IStruggleTelemetrySink? _sink;
    private readonly IDwellDetectorRegistry? _dwellRegistry;

    // State
    private DwellDetector? _dwellDetector;
    private PhysicalPoint? _manualSelectionCenter;
    private double? _manualSelectionRadius;
    private long _manualSelectionStartTimestamp;
    private bool _gracePeriodActive;
    private bool _isActive;

    public ManualSelectionDwellService(
        IMessenger messenger,
        ILogger<ManualSelectionDwellService> logger,
        EyeCtrl.Core.Settings.EyeCtrlSettings settings,
        Dispatcher dispatcher,
        InteractionManager interactionManager,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
    {
        _messenger = messenger;
        _logger = logger;
        _settings = settings;
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;
        _sink = sink;
        _dwellRegistry = dwellRegistry;

        // Register message handlers
        _messenger.Register<StartManualSelectionMessage>(this, (r, m) => OnManualSelectionStarted(m));
        _messenger.Register<EndManualSelectionMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<ManualSelectionHiddenMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<GazeCursorMovedMessage>(this, (r, m) => OnGazeMoved(m));
    }

    private void OnManualSelectionStarted(StartManualSelectionMessage message)
    {
        // Initialize state
        _manualSelectionCenter = message.Center;
        _manualSelectionRadius = message.Radius * DpiHelper.ScaleX; // Store physical pixel radius
        _isActive = true;
        _gracePeriodActive = false;
        _manualSelectionStartTimestamp = 0;

        // Create DwellDetector with cooldown disabled (we handle grace period separately)
        _dwellDetector = new DwellDetector(cooldownMs: 0, context: DwellContext.ManualSelection, sink: _sink);

        // Subscribe to dwell events
        _dwellDetector.TargetActivated += OnDwellActivated;
        _dwellDetector.TargetProgress += OnDwellProgress;

        // Create a virtual dwell target at the initial gaze position
        // The radius is scaled by magnification since we're in ManualSelection space
        var scaledRadius = _settings.Dwell.FixationRadiusPixels / message.Magnification;
        var dwellTime = _settings.Dwell.ManualSelectionActionMenuDwellMs;

        var target = new DwellTarget(
            ManualSelectionDwellTargetId,
            message.GazePoint, // Initial position (will be updated as gaze moves)
            scaledRadius,
            dwellTime);

        _dwellDetector.AddTarget(target);

        // Per-session registration keyed by this service: re-registering replaces the previous
        // session's entry (owner-keyed registry), so only the live detector is ever routed to.
        _dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _dwellDetector, DwellContext.ManualSelection,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));

        _logger.LogDebug("ManualSelection dwell tracking started: center=({X}, {Y}), radius={Radius}",
            _manualSelectionCenter.Value.X, _manualSelectionCenter.Value.Y, message.Radius);
    }

    private void OnManualSelectionEnded()
    {
        // Cleanup detector
        if (_dwellDetector != null)
        {
            // Unregister BEFORE nulling: the registry entry holds the detector through its applier,
            // and a detector from a finished session must not receive any further routed maps.
            _dwellRegistry?.Unregister(this);
            _dwellDetector.TargetActivated -= OnDwellActivated;
            _dwellDetector.TargetProgress -= OnDwellProgress;
            _dwellDetector.ClearTargets();
            _dwellDetector = null;
        }

        // Reset state
        _isActive = false;
        _gracePeriodActive = false;
        _manualSelectionCenter = null;
        _manualSelectionRadius = null;

        _logger.LogDebug("ManualSelection dwell tracking stopped");
    }

    private void OnGazeMoved(GazeCursorMovedMessage message)
    {
        // Early exit if not active or wrong state
        if (!_isActive ||
            _interactionManager.CurrentState != InteractionState.ManualSelectionActive ||
            _dwellDetector == null)
            return;

        // Gap-held sample: the position is frozen, so it must not advance dwell. Skipping the whole
        // sample also keeps the grace period honest — it starts at the first REAL sample rather than
        // at a held timestamp — and the indicator simply stays where it was, since the point is
        // unchanged.
        if (message.IsInGap)
            return;

        var pt = message.Position;
        var ts = message.TimestampMicroseconds;

        // Track last gaze point for dwell completion
        _lastGazePoint = pt;

        // 1. Update indicator position (even during grace period)
        UpdateIndicatorPosition(pt);

        // 2. Handle grace period
        if (!_gracePeriodActive)
        {
            _manualSelectionStartTimestamp = ts;
            _gracePeriodActive = true;
        }

        var gracePeriodUs = _settings.ManualSelection.ManualSelectionGracePeriodMs * MathConstants.MicrosecondsPerMillisecond;
        if ((ts - _manualSelectionStartTimestamp) < gracePeriodUs)
            return;

        // 3. Bounds check
        if (!IsWithinManualSelectionBounds(pt))
        {
            ResetDwell();
            return;
        }

        // 4. Update dwell target position to follow gaze
        _dwellDetector.UpdateTargetCenter(ManualSelectionDwellTargetId, pt);

        // 5. Update dwell detector
        _dwellDetector.Update(pt, ts);
    }

    /// <param name="handler">
    /// Short discriminator for the failing call site. It is a structured property, not part of
    /// the message template: collapsing the five call sites onto a content-free <c>"{Context}"</c>
    /// template would leave log queries nothing to match on but the rendered line.
    /// </param>
    private void DispatchSafe(Action action, string handler)
    {
        void Run()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualSelection dwell service: {Handler} failed", handler);
            }
        }

        if (_dispatcher.CheckAccess())
            Run();
        else
            _dispatcher.BeginInvoke(Run);
    }

    private void OnDwellActivated(string targetId)
    {
        if (targetId != ManualSelectionDwellTargetId) return;

        DispatchSafe(() =>
        {
            // The session can end between the detector raising TargetActivated and this
            // dispatched continuation running — OnManualSelectionEnded nulls _dwellDetector —
            // and completing into a torn-down ManualSelection would commit a click the user
            // already backed out of. Bail out instead.
            if (_dwellDetector == null) return;

            // We need to get the actual target to retrieve its center
            // Since DwellDetector doesn't expose targets, we'll use the last gaze position
            // This is acceptable because dwell activation happens when gaze is on target
            _logger.LogInformation("ManualSelection dwell completed");

            // Reset indicator
            _messenger.Send(new ResetManualSelectionIndicatorMessage());

            // We don't have access to the exact point, but we can send a completion message
            // The EventCoordinator will need to track the last known gaze position
            // For now, use the ManualSelection center as fallback (this will be improved)
            _messenger.Send(new ManualSelectionDwellCompletedMessage(_lastGazePoint ?? _manualSelectionCenter!.Value));
        }, "dwell-activated");
    }

    private void OnDwellProgress(string targetId, double progress)
    {
        if (targetId != ManualSelectionDwellTargetId) return;

        DispatchSafe(() => UpdateIndicatorProgress(progress), "dwell-progress");
    }

    private PhysicalPoint? _lastGazePoint;

    private bool IsWithinManualSelectionBounds(PhysicalPoint gazePoint)
    {
        if (_manualSelectionCenter == null || _manualSelectionRadius == null) return false;

        var dx = gazePoint.X - _manualSelectionCenter.Value.X;
        var dy = gazePoint.Y - _manualSelectionCenter.Value.Y;
        return (dx * dx + dy * dy) <= (_manualSelectionRadius.Value * _manualSelectionRadius.Value);
    }

    private void ResetDwell()
    {
        _dwellDetector?.ResetAllState();

        DispatchSafe(() => _messenger.Send(new ResetManualSelectionIndicatorMessage()), "indicator-reset");
    }

    private void UpdateIndicatorPosition(PhysicalPoint gazePoint)
    {
        DispatchSafe(() => _messenger.Send(new UpdateManualSelectionIndicatorPositionMessage(gazePoint)), "indicator-position");
    }

    private void UpdateIndicatorProgress(double progress)
    {
        DispatchSafe(() => _messenger.Send(new UpdateManualSelectionIndicatorProgressMessage(progress)), "indicator-progress");
    }

    public void Dispose()
    {
        OnManualSelectionEnded();
        _messenger.UnregisterAll(this);
    }
}
