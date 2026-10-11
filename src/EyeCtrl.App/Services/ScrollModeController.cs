namespace EyeCtrl.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Manages the scroll-mode lifecycle: entering, exiting, zoom dismissal, and feedback updates.
/// Extracted from <see cref="EventCoordinator"/> to reduce its responsibilities.
/// </summary>
public class ScrollModeController : IDisposable
{
    // Inset (logical px) from the right screen edge for the scroll stop button center.
    // The bar additionally reserves button radius + margin on the right so the button
    // never extends past the desktop edge.
    private const double ScrollStopInsetLogical = 50;

    /// <summary>
    /// Builds the scroll-stop <see cref="EdgeButtonDefinition"/> used at the start of scroll mode.
    /// Always emits <see cref="UIConstants.ButtonRadius"/> so the visual + hit-target match the
    /// pre-unified-bar 50x50 ellipse. Static and internal so a regression test pins the radius.
    /// </summary>
    internal static EdgeButtonDefinition CreateScrollStopButtonDefinition(
        int dwellMs, double verticalOffset)
        => new(
            Id: "scroll-stop",
            Symbol: "\u23F9",
            DwellMs: dwellMs,
            Activate: m => m.Send(new ExitScrollModeMessage()),
            VerticalOffset: verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius,
            AccessibleName: StringTable.Current.T("overlays.edge.stopScrolling"));

    private readonly EventCoordinationContext _context;
    private readonly Func<IScrollOverlaySurface> _createOverlayWindow;
    private ScrollOverlayViewModel? _scrollOverlayVm;
    private IScrollOverlaySurface? _scrollOverlayWindow;
    private ScrollWorker? _scrollWorker;
    private bool _scrollModeActive;
    private bool _disposed;

    public bool IsActive => _scrollModeActive;

    public ScrollModeController(EventCoordinationContext context)
        : this(context, () => new ScrollOverlayWindow())
    {
    }

    /// <summary>Test seam: supplies the overlay window, so a test can run the scroll lifecycle
    /// headlessly without putting a real window on screen.</summary>
    internal ScrollModeController(EventCoordinationContext context, Func<IScrollOverlaySurface> createOverlayWindow)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _createOverlayWindow = createOverlayWindow ?? throw new ArgumentNullException(nameof(createOverlayWindow));
        _context.InteractionManager.StateChanged += OnInteractionStateChanged;
        _context.Messenger.Register<ScrollModeController, ExitScrollModeMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => _context.InteractionManager.ExitScrollMode()));
    }

    // StateChanged arrives on whichever thread wrote the state — the gaze thread included (a
    // lost-gaze exit, a fixation edge) — while Enter/Exit create and close WPF windows and a
    // DispatcherTimer. Off the UI thread, marshal; on it, run inline so the dispatcher entry path
    // (the action menu's Scroll) keeps its synchronous behaviour.
    private void OnInteractionStateChanged(InteractionState _)
    {
        if (_context.Dispatcher.CheckAccess())
            ApplyCurrentState();
        else
            _context.Dispatcher.BeginInvoke(ApplyCurrentState);
    }

    // Decides from the state as it is NOW, not the event's payload: a marshaled edge can be stale
    // by the time it runs — a gaze-thread Fixation edge queued just before the action menu entered
    // scroll mode inline would otherwise tear down a live scroll mode, leaving Core in ScrollMode
    // with no window and no Stop button (ticket 02 review F2).
    private void ApplyCurrentState()
    {
        if (_disposed) return;
        if (_context.InteractionManager.CurrentState == InteractionState.ScrollMode)
            EnterScrollMode();
        else if (_scrollModeActive)
            ExitScrollMode();
    }

    private void EnterScrollMode()
    {
        if (_context == null || _scrollModeActive) return;

        // The caller saw ScrollMode; the anchor is the mode's own payload. Bail rather than throw if
        // it is missing (cleared on exit).
        if (_context.InteractionManager.ScrollAnchor is not { } anchor)
            return;

        _context.InputSimulator.MoveCursor(anchor);

        _context.OverlayManager.CursorCircle.Hide();

        var edgeAnchor = ScreenGeometry.RightEdgeAnchor(ScrollStopInsetLogical, UIConstants.ButtonRadius);
        var scrollStopDwell = _context.Settings.Dwell.ScrollStopButtonDwellMs;
        var stopDef = CreateScrollStopButtonDefinition(scrollStopDwell, verticalOffset: 0);
        _context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { stopDef }, edgeAnchor);

        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
        _scrollOverlayVm.EnterScrollMode(anchor, edgeAnchor);

        _scrollOverlayWindow = _createOverlayWindow();
        _scrollOverlayWindow.Show(_scrollOverlayVm);

        _scrollWorker = new ScrollWorker(_context.InputSimulator);
        _scrollWorker.Start(anchor);

        _scrollOverlayVm.TargetVelocityChanged += OnScrollTargetVelocityChanged;
        _scrollOverlayVm.FeedbackChanged += OnScrollFeedbackChanged;

        _scrollModeActive = true;
    }

    private void OnScrollTargetVelocityChanged(double vx, double vy)
        => _scrollWorker?.UpdateTargetVelocity(vx, vy);

    private void OnScrollFeedbackChanged()
    {
        if (_scrollOverlayWindow == null || _scrollOverlayVm == null) return;
        _scrollOverlayWindow.UpdateFeedback(
            _scrollOverlayVm.AnchorDotOffsetX,
            _scrollOverlayVm.AnchorDotOffsetY,
            _scrollOverlayVm.VelocityMagnitude);
    }

    private void ExitScrollMode()
    {
        _context.OverlayManager.EdgeButtonBar.Hide();

        _scrollWorker?.Stop();
        _scrollWorker?.Dispose();
        _scrollWorker = null;

        if (_scrollOverlayVm != null)
        {
            _scrollOverlayVm.TargetVelocityChanged -= OnScrollTargetVelocityChanged;
            _scrollOverlayVm.FeedbackChanged -= OnScrollFeedbackChanged;
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
        _disposed = true;
        _context.InteractionManager.StateChanged -= OnInteractionStateChanged;
    }
}

/// <summary>The scroll overlay window as <see cref="ScrollModeController"/> drives it.</summary>
internal interface IScrollOverlaySurface
{
    void Show(ScrollOverlayViewModel vm);
    void UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude);
    void Close();
}
