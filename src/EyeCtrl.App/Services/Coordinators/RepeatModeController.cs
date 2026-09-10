namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the locked repeat-click mode's app surface: the right-edge stop button, the UI-thread
/// tick timer that re-fires the locked click at the live stable centroid, and the WM_KEYDOWN /
/// VK_ESCAPE thread filter that ends an in-flight repeat. Mirrors <see cref="ScrollModeController"/>
/// (state-triggered entry, right-edge stop) and <see cref="DragCoordinator"/> (escape hatch) —
/// a separate coordinator because the state machine only owns WHEN repeats are legal
/// (<c>InteractionManager.FireRepeatTick</c>), not the cadence or the chrome.
///
/// <para>
/// <b>The ESC filter is a weak hatch</b> (review PR #139 finding 2):
/// <see cref="ComponentDispatcher.ThreadFilterMessage"/> only sees messages for EyeCtrl's own
/// windows, every overlay is non-activating, and repeat mode exists to click into OTHER apps —
/// so while the mode is doing its job, a caregiver's ESC lands in the foreground app, not here.
/// Inherited verbatim from <see cref="DragCoordinator"/>; a real global hatch needs a
/// WH_KEYBOARD_LL hook in EyeCtrl.Platform (follow-up). The gaze-reachable exits are the stop
/// button and the lost-gaze auto-exit in <c>InteractionManager.NoteGazeSample</c>.
/// </para>
/// </summary>
internal sealed class RepeatModeController : CoordinatorBase
{
    private const double StopInsetLogical = 50;

    /// <summary>
    /// Margin factor on the stop button's radius for the tick-suppression zone. The click lands
    /// at the fixation centroid, which trails the gaze while the user dwells the stop button —
    /// 1.5× covers that trail so no tick escapes the zone mid-dwell.
    /// </summary>
    private const double StopZoneMarginFactor = 1.5;

    /// <summary>
    /// Builds the repeat-stop <see cref="EdgeButtonDefinition"/>. Static and internal so a
    /// regression test can pin it without constructing the full coordination context — the same
    /// pattern as <see cref="ScrollModeController.CreateScrollStopButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateRepeatStopButtonDefinition(
        int dwellMs, double verticalOffset)
        => new(
            Id: "repeat-stop",
            Symbol: "\u23F9",
            DwellMs: dwellMs,
            Activate: m => m.Send(new ExitRepeatModeMessage()),
            VerticalOffset: verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius);

    /// <summary>
    /// Whether <paramref name="point"/> sits inside the right-edge stop button's suppression
    /// zone (review PR #139 finding 3). The bar is click-through, so a tick aimed at the stop
    /// button would fire the locked click THROUGH it onto whatever owns the right screen edge —
    /// during the very dwell that is trying to end the mode.
    /// </summary>
    internal static bool IsInStopButtonZone(
        PhysicalPoint point, PhysicalPoint stopCenterPhysical, double stopRadiusPhysical)
    {
        if (stopRadiusPhysical <= 0) return false;
        var zone = stopRadiusPhysical * StopZoneMarginFactor;
        return DistanceSquared(point, stopCenterPhysical) <= zone * zone;
    }

    private static double DistanceSquared(PhysicalPoint a, PhysicalPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private bool _active;
    private PhysicalPoint _stopCenterPhysical;
    private double _stopRadiusPhysical;

    protected override void OnInitialize()
    {
        var context = Context!;
        context.InteractionManager.StateChanged += OnStateChanged;
        context.Messenger.Register<RepeatModeController, ExitRepeatModeMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => context.InteractionManager.ExitRepeatMode()));
        // The Modes menu's entry path (D20/D24): keeps all repeat-mode message handling here.
        // EnterRepeatMode is itself FreeLook-guarded in Core, so an entry request in another
        // state is a no-op rather than a state trample.
        context.Messenger.Register<RepeatModeController, EnterRepeatModeMessage>(this, (_, m) =>
            context.Dispatcher.BeginInvoke(() =>
            {
                var im = context.InteractionManager;
                im.RepeatDwellMs = context.Settings.Repeat.DwellMs;
                im.EnterRepeatMode(m.Action);
            }));
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == 0x0100 && (int)msg.wParam == 0x1B) // WM_KEYDOWN, VK_ESCAPE
        {
            if (Context?.InteractionManager.CurrentState == InteractionState.RepeatMode)
            {
                Context.Messenger.Send(new ExitRepeatModeMessage());
                handled = true;
            }
        }
    }

    private void OnStateChanged(InteractionState state)
    {
        // StateChanged can arrive on the gaze thread — repeat mode's lost-gaze auto-exit fires
        // from InteractionManager.NoteGazeSample inside GazeEngine.OnGazePoint — while Enter/Exit
        // touch overlay windows. Both must run on the UI thread.
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (state == InteractionState.RepeatMode)
                Enter();
            else if (_active)
                Exit();
        });
    }

    private void Enter()
    {
        if (_active) return;
        var context = Context!;

        var edgeAnchor = ScreenGeometry.RightEdgeAnchor(StopInsetLogical, UIConstants.ButtonRadius);
        _stopCenterPhysical = edgeAnchor;
        _stopRadiusPhysical = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);
        var stopDef = CreateRepeatStopButtonDefinition(
            context.Settings.Repeat.StopButtonDwellMs, verticalOffset: 0);
        context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { stopDef }, edgeAnchor);

        // D26: chrome suppression zones — the locked-action dwell must never fire on EyeCtrl's
        // own surfaces (toolbar band incl. the modes-menu area below it, stop-button zone);
        // the chrome keeps its own dwell semantics there.
        var stopZoneRadius = _stopRadiusPhysical * StopZoneMarginFactor;
        context.InteractionManager.SetRepeatSuppressionZones(
            BuildToolbarBandRect(context.Settings.Toolbar.Size),
            new Core.Models.PhysicalRect(
                _stopCenterPhysical.X - stopZoneRadius,
                _stopCenterPhysical.Y - stopZoneRadius,
                stopZoneRadius * 2,
                stopZoneRadius * 2));

        _active = true;
    }

    /// <summary>
    /// The toolbar band as a physical rect: full screen width, height = toolbar size + margins,
    /// extended to also cover the modes-menu panel that appears below it. Pure arithmetic from
    /// settings — matches the ModesMenu placement math, needs no window measurement.
    /// </summary>
    internal static Core.Models.PhysicalRect BuildToolbarBandRect(double toolbarSizeLogical)
    {
        var logicalW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var bandLogicalH = toolbarSizeLogical + 130;
        return new Core.Models.PhysicalRect(
            0, 0,
            DpiHelper.LogicalToPhysicalX(logicalW),
            DpiHelper.LogicalToPhysicalY(bandLogicalH));
    }

    private void Exit()
    {
        Context?.InteractionManager.SetRepeatSuppressionZones();
        Context?.OverlayManager.EdgeButtonBar.Hide();
        _active = false;
    }

    protected override void OnDispose()
    {
        if (_active) Exit();
        if (Context is null) return;
        Context.InteractionManager.StateChanged -= OnStateChanged;
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
    }
}
