namespace EyeCtrl.App.Services;

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>The production cursor reader (scroll start fix, F-S5's diagnosis — this settles
    /// H-A vs H-C in the next session). The exe is System-DPI-aware (no dpi manifest element), so
    /// on this owner's single 200 % monitor the POINT is physical px; on a mixed-DPI multi-monitor
    /// setup GetCursorPos would be virtualised (fixup 01, F6).</summary>
    private static PhysicalPoint? ReadCursorPosition() =>
        GetCursorPos(out var point) ? new PhysicalPoint(point.X, point.Y) : null;

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

    /// <summary>
    /// The scroll-stop button's slot: the fixed UPPER "out" slot, one radius-plus-half-gap above
    /// the shared right-edge anchor (negative Y, 60 DIP at the shipped sizes). Exposed so a test
    /// can pin the production placement, not just the factory's parameter.
    /// </summary>
    internal static double StopButtonVerticalOffsetPhysical =>
        -DpiHelper.LogicalToPhysicalY(UIConstants.EdgeButtonSlotOffset);

    private readonly EventCoordinationContext _context;
    private readonly Func<IScrollOverlaySurface> _createOverlayWindow;
    private readonly Func<PhysicalPoint?> _cursorReader;
    private ScrollOverlayViewModel? _scrollOverlayVm;
    private IScrollOverlaySurface? _scrollOverlayWindow;
    private ScrollWorker? _scrollWorker;
    private bool _scrollModeActive;
    private bool _disposed;
    private long _enteredAtMs;

    /// <summary>The live worker, so a test can drive its ticks deterministically (G5).</summary>
    internal ScrollWorker? Worker => _scrollWorker;

    /// <summary>The live overlay VM while the mode runs — test seam for the entry wiring (the
    /// stop button's drawn slot and its stop zone must agree).</summary>
    internal ScrollOverlayViewModel? OverlayVm => _scrollOverlayVm;

    public bool IsActive => _scrollModeActive;

    public ScrollModeController(EventCoordinationContext context)
        : this(context, () => new ScrollOverlayWindow())
    {
    }

    /// <summary>Test seam: supplies the overlay window, so a test can run the scroll lifecycle
    /// headlessly without putting a real window on screen; <paramref name="cursorReader"/> fakes
    /// the OS cursor for the F-S5 lines.</summary>
    internal ScrollModeController(
        EventCoordinationContext context,
        Func<IScrollOverlaySurface> createOverlayWindow,
        Func<PhysicalPoint?>? cursorReader = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _createOverlayWindow = createOverlayWindow ?? throw new ArgumentNullException(nameof(createOverlayWindow));
        _cursorReader = cursorReader ?? ReadCursorPosition;
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

        // F-S1b (scroll start fix, the #219 pattern): OnActionSelected invokes HideActionMenu
        // SYNCHRONOUSLY and then raises the state change, but the hide it invoked has only been
        // POSTED — so the menu window AND the backdrop are still on screen at the first injected
        // action, the same shape #219 fixed for the drag and this path missed. ORDER IS
        // LOAD-BEARING: the menu VM goes false FIRST, because its coordinator listener is what
        // posts the backdrop's deferred hide that CompleteHideNow completes; the later, queued
        // hide sets an already-false property and raises nothing (idempotent). The overlay
        // manager is read through a NULLABLE local, as #220 did, so harnesses without one keep
        // their meaning.
        if (_context.ActionMenuVM is { } menuVm)
            menuVm.IsVisible = false;
        IOverlayManager? overlays = _context.OverlayManager;
        var backdrop = overlays?.GlassBackdrop;
        backdrop?.CompleteHideNow();
        bool backdropVisible = backdrop?.IsVisible ?? false;

        // F7 (fixup 01): read the cursor BEFORE the park, so the entry line shows where the
        // external cursor writer (TD Control / Gaze Point, H-A) had it — after MoveCursor it is
        // always the anchor and carries no information.
        var entryCursor = _cursorReader();

        _enteredAtMs = Environment.TickCount64;

        _context.InputSimulator.MoveCursor(anchor);

        _context.OverlayManager.CursorCircle.Hide();

        var edgeAnchor = ScreenGeometry.RightEdgeAnchor(ScrollStopInsetLogical, UIConstants.ButtonRadius);
        var stopOffset = StopButtonVerticalOffsetPhysical;
        var scrollStopDwell = _context.Settings.Dwell.ScrollStopButtonDwellMs;
        var stopDef = CreateScrollStopButtonDefinition(scrollStopDwell, stopOffset);
        _context.OverlayManager.EdgeButtonBar.ShowButtons(
            new[] { stopDef }, edgeAnchor, EdgeBarOwner.Scroll);

        // The button now sits in the upper out slot, so the scroll engine's stop zone must follow
        // it: the zone is where the button is drawn, and leaving it on the anchor would let the
        // page keep scrolling while the user dwells their way out.
        var stopButtonCenter = edgeAnchor + new PhysicalPoint(0, stopOffset);

        _scrollOverlayVm = new ScrollOverlayViewModel(
            _context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell,
            line => Log.Information("{Scroll}", line));
        _scrollOverlayVm.EnterScrollMode(anchor, stopButtonCenter);

        // F-S5 (E5): the entry diagnosis — the anchor, the two thresholds, where the OS cursor was
        // before the park (H-A's discriminator), and what the backdrop was at the injection point.
        Log.Information(
            "{ScrollModeLine}", DescribeEntry(anchor, _scrollOverlayVm, entryCursor, backdropVisible));

        _scrollOverlayWindow = _createOverlayWindow();
        _scrollOverlayWindow.Show(_scrollOverlayVm);

        _scrollWorker = new ScrollWorker(_context.InputSimulator);
        _scrollWorker.Start(
            anchor,
            isArmed: () => _scrollOverlayVm?.IsArmed ?? false,
            cursorReader: _cursorReader,
            nearestPx: () => _scrollOverlayVm?.NearestPx ?? -1);

        _scrollOverlayVm.TargetVelocityChanged += OnScrollTargetVelocityChanged;
        _scrollOverlayVm.FeedbackChanged += OnScrollFeedbackChanged;

        _scrollModeActive = true;
    }

    /// <summary>The F-S5 entry line. Invariant: the session scripts parse it, and a German decimal
    /// comma must not appear.</summary>
    internal static string DescribeEntry(
        PhysicalPoint anchor, ScrollOverlayViewModel vm, PhysicalPoint? cursor, bool backdropVisible)
    {
        string cursorText = DescribeCursor(cursor);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Scroll mode: anchor=({0:F0},{1:F0}) deadZone={2:F0}px stopZone={3:F0}px startZone={4:F0}px {5} [BackdropVisible={6}]",
            anchor.X, anchor.Y, vm.DeadZoneRadiusPx, vm.StopZoneRadiusPx, vm.StartRadiusPx, cursorText, backdropVisible);
    }

    /// <summary>Renders the cursor fields tolerantly: the reader can fail (an injected harness, a
    /// locked desktop), and the line must still say so rather than vanish.</summary>
    internal static string DescribeCursor(PhysicalPoint? cursor) =>
        cursor is { } c
            ? string.Format(CultureInfo.InvariantCulture, "cursor=({0:F0},{1:F0})", c.X, c.Y)
            : "cursor=(?,?)";

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
        // F-S5 (E5): the exit diagnosis, read before the teardown clears the state it reports.
        Log.Information(
            "{ScrollModeLine}",
            string.Format(
                CultureInfo.InvariantCulture,
                "Scroll mode exited after {0} ms (wheels {1}, armed {2})",
                Environment.TickCount64 - _enteredAtMs,
                _scrollWorker?.TotalWheels ?? 0,
                _scrollOverlayVm?.IsArmed ?? false));

        _context.OverlayManager.EdgeButtonBar.Hide(EdgeBarOwner.Scroll);

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
