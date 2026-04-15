namespace EyeCtrl.App.Services;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Manages zoom dwell detection and gaze indicator updates during zoom mode.
/// Subscribes to GazeCursorMovedMessage and emits ZoomFixationCompletedMessage.
/// Extracts the 130+ lines of zoom dwell logic from App.xaml.cs into a testable service.
/// </summary>
public class ZoomDwellService : IDisposable
{
    private const string ZoomDwellTargetId = "zoom_dwell_target";

    private readonly IMessenger _messenger;
    private readonly ILogger<ZoomDwellService> _logger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;

    // State
    private DwellDetector? _dwellDetector;
    private PhysicalPoint? _zoomCenter;
    private double? _zoomRadius;
    private long _zoomStartTimestamp;
    private bool _gracePeriodActive;
    private bool _isActive;

    public ZoomDwellService(
        IMessenger messenger,
        ILogger<ZoomDwellService> logger,
        EyeCtrl.Core.Settings.EyeCtrlSettings settings,
        Dispatcher dispatcher,
        InteractionManager interactionManager)
    {
        _messenger = messenger;
        _logger = logger;
        _settings = settings;
        _dispatcher = dispatcher;
        _interactionManager = interactionManager;

        // Register message handlers
        _messenger.Register<StartZoomMessage>(this, (r, m) => OnZoomStarted(m));
        _messenger.Register<EndZoomMessage>(this, (r, m) => OnZoomEnded());
        _messenger.Register<ZoomHiddenMessage>(this, (r, m) => OnZoomEnded());
        _messenger.Register<GazeCursorMovedMessage>(this, (r, m) => OnGazeMoved(m));
    }

    private void OnZoomStarted(StartZoomMessage message)
    {
        // Initialize state
        _zoomCenter = message.Center;
        _zoomRadius = message.Radius * DpiHelper.ScaleX; // Store physical pixel radius
        _isActive = true;
        _gracePeriodActive = false;
        _zoomStartTimestamp = 0;

        // Create DwellDetector with cooldown disabled (we handle grace period separately)
        _dwellDetector = new DwellDetector(cooldownMs: 0);

        // Subscribe to dwell events
        _dwellDetector.TargetActivated += OnDwellActivated;
        _dwellDetector.TargetProgress += OnDwellProgress;

        // Create a virtual dwell target at the initial gaze position
        // The radius is scaled by magnification since we're in zoom space
        var scaledRadius = _settings.Dwell.FixationRadiusPixels / message.Magnification;
        var dwellTime = _settings.Dwell.ZoomActionMenuDwellMs;

        var target = new DwellTarget(
            ZoomDwellTargetId,
            message.GazePoint, // Initial position (will be updated as gaze moves)
            scaledRadius,
            dwellTime);

        _dwellDetector.AddTarget(target);

        _logger.LogDebug("Zoom dwell tracking started: center=({X}, {Y}), radius={Radius}",
            _zoomCenter.Value.X, _zoomCenter.Value.Y, message.Radius);
    }

    private void OnZoomEnded()
    {
        // Cleanup detector
        if (_dwellDetector != null)
        {
            _dwellDetector.TargetActivated -= OnDwellActivated;
            _dwellDetector.TargetProgress -= OnDwellProgress;
            _dwellDetector.ClearTargets();
            _dwellDetector = null;
        }

        // Reset state
        _isActive = false;
        _gracePeriodActive = false;
        _zoomCenter = null;
        _zoomRadius = null;

        _logger.LogDebug("Zoom dwell tracking stopped");
    }

    private void OnGazeMoved(GazeCursorMovedMessage message)
    {
        // Early exit if not active or wrong state
        if (!_isActive ||
            _interactionManager.CurrentState != InteractionState.ZoomSelectionActive ||
            _dwellDetector == null)
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
            _zoomStartTimestamp = ts;
            _gracePeriodActive = true;
        }

        var gracePeriodUs = _settings.Zoom.ZoomGracePeriodMs * MathConstants.MicrosecondsPerMillisecond;
        if ((ts - _zoomStartTimestamp) < gracePeriodUs)
            return;

        // 3. Bounds check
        if (!IsWithinZoomBounds(pt))
        {
            ResetDwell();
            return;
        }

        // 4. Update dwell target position to follow gaze
        _dwellDetector.UpdateTargetCenter(ZoomDwellTargetId, pt);

        // 5. Update dwell detector
        _dwellDetector.Update(pt, ts);
    }

    private void InvokeOnDispatcherIfNeeded(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    private void OnDwellActivated(string targetId)
    {
        if (targetId != ZoomDwellTargetId) return;

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                // Get the current dwell target position as the precise fixation point
                var target = _dwellDetector?.GetProgress(ZoomDwellTargetId);
                if (_dwellDetector == null) return;

                // We need to get the actual target to retrieve its center
                // Since DwellDetector doesn't expose targets, we'll use the last gaze position
                // This is acceptable because dwell activation happens when gaze is on target
                _logger.LogInformation("Zoom dwell completed");

                // Reset indicator
                _messenger.Send(new ResetZoomIndicatorMessage());

                // We don't have access to the exact point, but we can send a completion message
                // The EventCoordinator will need to track the last known gaze position
                // For now, use the zoom center as fallback (this will be improved)
                _messenger.Send(new ZoomFixationCompletedMessage(_lastGazePoint ?? _zoomCenter!.Value));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom dwell activated handler");
            }
        });
    }

    private void OnDwellProgress(string targetId, double progress)
    {
        if (targetId != ZoomDwellTargetId) return;

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                // Update indicator progress
                UpdateIndicatorProgress(progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom dwell progress handler");
            }
        });
    }

    private PhysicalPoint? _lastGazePoint;

    private bool IsWithinZoomBounds(PhysicalPoint gazePoint)
    {
        if (_zoomCenter == null || _zoomRadius == null) return false;

        var dx = gazePoint.X - _zoomCenter.Value.X;
        var dy = gazePoint.Y - _zoomCenter.Value.Y;
        return (dx * dx + dy * dy) <= (_zoomRadius.Value * _zoomRadius.Value);
    }

    private void ResetDwell()
    {
        _dwellDetector?.ResetAllState();

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new ResetZoomIndicatorMessage());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting zoom indicator");
            }
        });
    }

    private void UpdateIndicatorPosition(PhysicalPoint gazePoint)
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new UpdateZoomIndicatorPositionMessage(gazePoint));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating zoom indicator position");
            }
        });
    }

    private void UpdateIndicatorProgress(double progress)
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new UpdateZoomIndicatorProgressMessage(progress));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating zoom indicator progress");
            }
        });
    }

    public void Dispose()
    {
        OnZoomEnded();
        _messenger.UnregisterAll(this);
    }
}
