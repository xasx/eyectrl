namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the locked repeat-click mode's app surface: the right-edge stop button, the UI-thread
/// tick timer that re-fires the locked click at the live stable centroid, and the global
/// ESC-hatch subscription that ends an in-flight repeat (issue #141). Mirrors
/// <see cref="ScrollModeController"/> (state-triggered entry, right-edge stop) and
/// <see cref="DragCoordinator"/> (escape hatch) — a separate coordinator because the state
/// machine only owns WHEN repeats are legal (<c>InteractionManager.FireRepeatTick</c>), not
/// the cadence or the chrome.
///
/// <para>
/// ESC exits are gaze-independent: the hatch sees keystrokes system-wide, so a caregiver can
/// end the mode even while a third-party app holds the foreground. The gaze-reachable exits
/// are the stop button and the lost-gaze auto-exit in <c>InteractionManager.NoteGazeSample</c>.
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

    private bool _active;
    private PhysicalPoint _stopCenterPhysical;
    private double _stopRadiusPhysical;

    protected override void OnInitialize()
    {
        var context = Context!;
        context.InteractionManager.StateChanged += OnStateChanged;

        // Review PR #139 round 3 finding 1 — the entry guard's missing half. A Modes row is a
        // 700 ms dwell while fixation fires at 300 ms and nothing else suppresses it, so at row
        // activation the state was Fixation and the FreeLook-guarded EnterRepeatMode no-oped:
        // the mode could never start through its own UI. While the menu is visible we suppress
        // engine fixation and clear any live one (the KeyboardCoordinator pattern); without
        // this the row's dwell races a state the entry refuses.
        context.ModesMenuVM.PropertyChanged += OnModesMenuVMVisibilityChanged;
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
        context.EscapeHatch.EscapePressed += OnEscapePressed;
    }

    /// <summary>
    /// Pool thread (global hatch, issue #141): ESC pressed anywhere in the system ends the
    /// mode. Marshaled to the UI thread before the Send (review PR #142): Messenger delivers
    /// synchronously to every recipient, and a future recipient may not marshal — the send
    /// thread is the delivery thread. <c>ExitRepeatMode</c> is state-guarded, so an ESC
    /// outside the mode is a no-op either way.
    /// </summary>
    private void OnEscapePressed()
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (context.InteractionManager.CurrentState == InteractionState.RepeatMode)
                context.Messenger.Send(new ExitRepeatModeMessage());
        });
    }

    private void OnModesMenuVMVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var context = Context;
        if (context is null || e.PropertyName != nameof(EyeCtrl.ViewModels.ModesMenuViewModel.IsVisible)) return;

        context.Dispatcher.BeginInvoke(() =>
        {
            if (context.ModesMenuVM.IsVisible)
            {
                context.GazeEngine.SuppressFixation = true;
                // A fixation already in flight would leave the state at Fixation when the row
                // activates — end it, exactly as the keyboard does when it takes over gaze.
                context.InteractionManager.OnFixationEnded();
                // While the menu is up (picking, or exiting the mode) the extended band covers
                // it — otherwise the slim band leaves the lower rows dwell-to-act-fireable.
                if (_active)
                    ApplySuppressionZones(BuildMenuExtendedBandRect(context.Settings.Toolbar.Size));
            }
            else
            {
                context.GazeEngine.SuppressFixation = false;
                if (_active)
                    ApplySuppressionZones(BuildToolbarBandRect(context.Settings.Toolbar.Size));
            }
        });
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
            {
                Enter();
                // D30: the toolbar Modes button shows the locked action's glyph while the mode
                // runs and the stack glyph otherwise — the button doubles as the state display.
                context.Messenger.Send(new RepeatActionChangedMessage(
                    context.InteractionManager.RepeatAction));
            }
            else
            {
                if (_active)
                    Exit();
                context.Messenger.Send(new RepeatActionChangedMessage(null));
            }
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

        ApplySuppressionZones(BuildToolbarBandRect(context.Settings.Toolbar.Size));
        Serilog.Log.Information(
            "Repeat mode entered: {Action}, dwell {DwellMs} ms",
            context.InteractionManager.RepeatAction, context.Settings.Repeat.DwellMs);
        _active = true;
    }

    private void ApplySuppressionZones(params Core.Models.PhysicalRect[] zones)
    {
        var context = Context!;
        var stopZoneRadius = _stopRadiusPhysical * StopZoneMarginFactor;
        var all = zones.Concat(new[]
        {
            new Core.Models.PhysicalRect(
                _stopCenterPhysical.X - stopZoneRadius,
                _stopCenterPhysical.Y - stopZoneRadius,
                stopZoneRadius * 2,
                stopZoneRadius * 2),
        });
        context.InteractionManager.SetRepeatSuppressionZones(all.ToArray());
    }

    /// <summary>
    /// The toolbar band as a physical rect: full screen width, height = toolbar size + a short
    /// margin. Field round 2: the previous band extended +200 logical below the toolbar — on a
    /// scaled display that silenced dwell-to-act across the whole top quarter of the screen
    /// ("dwell stopped working"). The panel is only ever visible while PICKING (mode off) or
    /// EXITING, and the menu-visibility handler swaps in the extended band for those moments.
    /// </summary>
    internal static Core.Models.PhysicalRect BuildToolbarBandRect(double toolbarSizeLogical)
        => BuildBandRect(toolbarSizeLogical + 40);

    /// <summary>The extended band: toolbar + the full modes panel height (menu visible).</summary>
    internal static Core.Models.PhysicalRect BuildMenuExtendedBandRect(double toolbarSizeLogical)
        => BuildBandRect(toolbarSizeLogical * 2 + 60);

    private static Core.Models.PhysicalRect BuildBandRect(double bandLogicalH)
    {
        var logicalW = System.Windows.SystemParameters.PrimaryScreenWidth;
        return new Core.Models.PhysicalRect(
            0, 0,
            DpiHelper.LogicalToPhysicalX(logicalW),
            DpiHelper.LogicalToPhysicalY(bandLogicalH));
    }

    private void Exit()
    {
        Context?.InteractionManager.SetRepeatSuppressionZones();
        Context?.OverlayManager.EdgeButtonBar.Hide();
        Serilog.Log.Information("Repeat mode exited");
        _active = false;
    }

    protected override void OnDispose()
    {
        if (_active) Exit();
        if (Context is null) return;
        Context.InteractionManager.StateChanged -= OnStateChanged;
        Context.EscapeHatch.EscapePressed -= OnEscapePressed;
    }
}
