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
/// Manages zoom fixation detection and gaze indicator updates during zoom mode.
/// Subscribes to GazeCursorMovedMessage and emits ZoomFixationCompletedMessage.
/// Extracts the 130+ lines of zoom fixation logic from App.xaml.cs into a testable service.
/// </summary>
public class ZoomFixationService : IDisposable
{
    private readonly IMessenger _messenger;
    private readonly ILogger<ZoomFixationService> _logger;
    private readonly EyeCtrlSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly InteractionManager _interactionManager;

    // State
    private FixationDetector? _fixationDetector;
    private ScreenPoint? _zoomCenter;
    private long _zoomStartTimestamp;
    private bool _gracePeriodActive;
    private bool _isActive;

    public ZoomFixationService(
        IMessenger messenger,
        ILogger<ZoomFixationService> logger,
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
        _isActive = true;
        _gracePeriodActive = false;
        _zoomStartTimestamp = 0;

        // Create FixationDetector with scaled radius
        var scaledRadius = _settings.Dwell.FixationRadiusPixels / message.Magnification;
        _fixationDetector = new FixationDetector(
            scaledRadius,
            _settings.Dwell.FixationDetectionMs);

        // Subscribe to fixation events
        _fixationDetector.FixationStarted += OnFixationStarted;
        _fixationDetector.FixationEnded += OnFixationEnded;

        _logger.LogDebug("Zoom fixation tracking started: center=({X}, {Y}), radius={Radius}",
            _zoomCenter.X, _zoomCenter.Y, message.Radius);
    }

    private void OnZoomEnded()
    {
        // Cleanup detector
        if (_fixationDetector != null)
        {
            _fixationDetector.FixationStarted -= OnFixationStarted;
            _fixationDetector.FixationEnded -= OnFixationEnded;
            _fixationDetector = null;
        }

        // Reset state
        _isActive = false;
        _gracePeriodActive = false;
        _zoomCenter = null;

        _logger.LogDebug("Zoom fixation tracking stopped");
    }

    private void OnGazeMoved(GazeCursorMovedMessage message)
    {
        // Early exit if not active or wrong state
        if (!_isActive ||
            _interactionManager.CurrentState != InteractionState.ZoomSelectionActive ||
            _fixationDetector == null)
            return;

        var pt = message.Position;
        var ts = message.TimestampMicroseconds;

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
            ResetFixation();
            return;
        }

        // 4. Update fixation detector
        _fixationDetector.Update(pt, ts);

        // 5. Update progress
        var progress = _fixationDetector.GetProgress();
        UpdateIndicatorProgress(progress);
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

    private void OnFixationStarted(ScreenPoint fixationPoint)
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                _logger.LogInformation("Zoom fixation completed at ({X:F0}, {Y:F0})",
                    fixationPoint.X, fixationPoint.Y);

                // Hide indicator
                _messenger.Send(new ResetZoomIndicatorMessage());

                // Emit completion message (App.xaml.cs will handle coordinate conversion)
                _messenger.Send(new ZoomFixationCompletedMessage(fixationPoint));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom fixation started handler");
            }
        });
    }

    private void OnFixationEnded()
    {
        InvokeOnDispatcherIfNeeded(() =>
        {
            try
            {
                // Only reset if indicator still visible (not already hidden by completion)
                _messenger.Send(new ResetZoomIndicatorMessage());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in zoom fixation ended handler");
            }
        });
    }

    private bool IsWithinZoomBounds(ScreenPoint gazePoint)
    {
        if (_zoomCenter == null) return false;

        var physicalZoomRadius = _settings.Zoom.CircleRadiusPixels * DpiHelper.ScaleX;
        var dx = gazePoint.X - _zoomCenter.Value.X;
        var dy = gazePoint.Y - _zoomCenter.Value.Y;
        return (dx * dx + dy * dy) <= (physicalZoomRadius * physicalZoomRadius);
    }

    private void ResetFixation()
    {
        _fixationDetector?.Reset();

        // Note: FixationDetector.Reset() does NOT fire FixationEnded,
        // so we must manually reset the UI
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

    private void UpdateIndicatorPosition(ScreenPoint gazePoint)
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
