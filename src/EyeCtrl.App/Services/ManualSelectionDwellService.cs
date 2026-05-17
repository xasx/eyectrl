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

    // State
    private DwellDetector? _dwellDetector;
    private PhysicalPoint? _ManualSelectionCenter;
    private double? _ManualSelectionRadius;
    private long _ManualSelectionStartTimestamp;
    private bool _gracePeriodActive;
    private bool _isActive;

    public ManualSelectionDwellService(
        IMessenger messenger,
        ILogger<ManualSelectionDwellService> logger,
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
        _messenger.Register<StartManualSelectionMessage>(this, (r, m) => OnManualSelectionStarted(m));
        _messenger.Register<EndManualSelectionMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<ManualSelectionHiddenMessage>(this, (r, m) => OnManualSelectionEnded());
        _messenger.Register<GazeCursorMovedMessage>(this, (r, m) => OnGazeMoved(m));
    }

    private void OnManualSelectionStarted(StartManualSelectionMessage message)
    {
        // Initialize state
        _ManualSelectionCenter = message.Center;
        _ManualSelectionRadius = message.Radius * DpiHelper.ScaleX; // Store physical pixel radius
        _isActive = true;
        _gracePeriodActive = false;
        _ManualSelectionStartTimestamp = 0;

        // Create DwellDetector with cooldown disabled (we handle grace period separately)
        _dwellDetector = new DwellDetector(cooldownMs: 0);

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

        _logger.LogDebug("ManualSelection dwell tracking started: center=({X}, {Y}), radius={Radius}",
            _ManualSelectionCenter.Value.X, _ManualSelectionCenter.Value.Y, message.Radius);
    }

    private void OnManualSelectionEnded()
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
        _ManualSelectionCenter = null;
        _ManualSelectionRadius = null;

        _logger.LogDebug("ManualSelection dwell tracking stopped");
    }

    private void OnGazeMoved(GazeCursorMovedMessage message)
    {
        // Early exit if not active or wrong state
        if (!_isActive ||
            _interactionManager.CurrentState != InteractionState.ManualSelectionActive ||
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
            _ManualSelectionStartTimestamp = ts;
            _gracePeriodActive = true;
        }

        var gracePeriodUs = _settings.ManualSelection.ManualSelectionGracePeriodMs * MathConstants.MicrosecondsPerMillisecond;
        if ((ts - _ManualSelectionStartTimestamp) < gracePeriodUs)
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
        if (targetId != ManualSelectionDwellTargetId) return;

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                // Get the current dwell target position as the precise fixation point
                var target = _dwellDetector?.GetProgress(ManualSelectionDwellTargetId);
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
                _messenger.Send(new ManualSelectionDwellCompletedMessage(_lastGazePoint ?? _ManualSelectionCenter!.Value));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ManualSelection dwell activated handler");
            }
        });
    }

    private void OnDwellProgress(string targetId, double progress)
    {
        if (targetId != ManualSelectionDwellTargetId) return;

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                // Update indicator progress
                UpdateIndicatorProgress(progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ManualSelection dwell progress handler");
            }
        });
    }

    private PhysicalPoint? _lastGazePoint;

    private bool IsWithinManualSelectionBounds(PhysicalPoint gazePoint)
    {
        if (_ManualSelectionCenter == null || _ManualSelectionRadius == null) return false;

        var dx = gazePoint.X - _ManualSelectionCenter.Value.X;
        var dy = gazePoint.Y - _ManualSelectionCenter.Value.Y;
        return (dx * dx + dy * dy) <= (_ManualSelectionRadius.Value * _ManualSelectionRadius.Value);
    }

    private void ResetDwell()
    {
        _dwellDetector?.ResetAllState();

        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new ResetManualSelectionIndicatorMessage());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting ManualSelection indicator");
            }
        });
    }

    private void UpdateIndicatorPosition(PhysicalPoint gazePoint)
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new UpdateManualSelectionIndicatorPositionMessage(gazePoint));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating ManualSelection indicator position");
            }
        });
    }

    private void UpdateIndicatorProgress(double progress)
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _messenger.Send(new UpdateManualSelectionIndicatorProgressMessage(progress));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating ManualSelection indicator progress");
            }
        });
    }

    public void Dispose()
    {
        OnManualSelectionEnded();
        _messenger.UnregisterAll(this);
    }
}
