namespace EyeCtrl.App.Services;

using System;
using System.Windows.Forms;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// Manages the scroll-mode lifecycle: entering, exiting, zoom dismissal, and feedback updates.
/// Extracted from <see cref="EventCoordinator"/> to reduce its responsibilities.
/// </summary>
public class ScrollModeController : IDisposable
{
    private readonly EventCoordinationContext _context;
    private ScrollOverlayViewModel? _scrollOverlayVm;
    private ScrollOverlayWindow? _scrollOverlayWindow;
    private ScrollWorker? _scrollWorker;
    private bool _scrollModeActive;

    public bool IsActive => _scrollModeActive;

    public ScrollModeController(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.InteractionManager.StateChanged += OnInteractionStateChanged;
    }

    private void OnInteractionStateChanged(InteractionState state)
    {
        if (state == InteractionState.ScrollMode)
            EnterScrollMode();
        else if (_scrollModeActive)
            ExitScrollMode();
    }

    public void OnDismissZoom()
    {
        if (_context?.OverlayManager.ManualSelectionOverlay.IsVisible == true)
            _context.OverlayManager.ManualSelectionOverlay.Hide();
    }

    private void EnterScrollMode()
    {
        if (_context == null) return;

        var anchor = _context.InteractionManager.ScrollAnchor!.Value;

        _context.InputSimulator.MoveCursor(anchor);

        _context.OverlayManager.CursorCircle.Hide();

        var bounds = Screen.PrimaryScreen!.Bounds;
        var stopButtonCenter = new PhysicalPoint(
            bounds.Width - DpiHelper.LogicalToPhysicalX(50),
            bounds.Height / 2.0);

        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
        _scrollOverlayVm.EnterScrollMode(anchor, stopButtonCenter);
        _scrollOverlayVm.ExitRequested += OnScrollExitRequested;

        _scrollOverlayWindow = new ScrollOverlayWindow();
        _scrollOverlayWindow.Show(_scrollOverlayVm);

        _scrollWorker = new ScrollWorker(_context.InputSimulator);
        _scrollWorker.Start(anchor);

        _scrollOverlayVm.TargetVelocityChanged += OnScrollTargetVelocityChanged;
        _scrollOverlayVm.FeedbackChanged += OnScrollFeedbackChanged;

        _scrollModeActive = true;
    }

    private void OnScrollExitRequested()
        => _context?.Dispatcher.BeginInvoke(() => _context?.InteractionManager.ExitScrollMode());

    private void OnScrollTargetVelocityChanged(double vx, double vy)
        => _scrollWorker?.UpdateTargetVelocity(vx, vy);

    private void OnScrollFeedbackChanged()
    {
        if (_scrollOverlayWindow == null || _scrollOverlayVm == null) return;
        _scrollOverlayWindow.UpdateFeedback(
            _scrollOverlayVm.AnchorDotOffsetX,
            _scrollOverlayVm.AnchorDotOffsetY,
            _scrollOverlayVm.VelocityMagnitude,
            _scrollOverlayVm.StopButtonProgress);
    }

    private void ExitScrollMode()
    {
        _scrollWorker?.Stop();
        _scrollWorker?.Dispose();
        _scrollWorker = null;

        if (_scrollOverlayVm != null)
        {
            _scrollOverlayVm.TargetVelocityChanged -= OnScrollTargetVelocityChanged;
            _scrollOverlayVm.FeedbackChanged -= OnScrollFeedbackChanged;
            _scrollOverlayVm.ExitRequested -= OnScrollExitRequested;
            _scrollOverlayVm.ExitScrollMode();
            _scrollOverlayVm.Dispose();
            _scrollOverlayVm = null;
        }

        _scrollOverlayWindow?.Close();
        _scrollOverlayWindow = null;

        if (_context != null && _context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();

        _scrollModeActive = false;
    }

    public void Dispose()
    {
        if (_scrollModeActive) ExitScrollMode();
        _context.InteractionManager.StateChanged -= OnInteractionStateChanged;
    }
}
