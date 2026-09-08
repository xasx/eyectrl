namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the locked repeat-click mode's app surface: the right-edge stop button, the UI-thread
/// tick timer that re-fires the locked click at the live stable centroid, and the WM_KEYDOWN /
/// VK_ESCAPE thread filter that ends an in-flight repeat. Mirrors <see cref="ScrollModeController"/>
/// (state-triggered entry, right-edge stop) and <see cref="DragCoordinator"/> (escape hatch) —
/// a separate coordinator because the state machine only owns WHEN repeats are legal
/// (<c>InteractionManager.FireRepeatTick</c>), not the cadence or the chrome.
/// </summary>
internal sealed class RepeatModeController : CoordinatorBase
{
    private const double StopInsetLogical = 50;

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

    private DispatcherTimer? _timer;
    private bool _active;

    protected override void OnInitialize()
    {
        var context = Context!;
        context.InteractionManager.StateChanged += OnStateChanged;
        context.Messenger.Register<RepeatModeController, ExitRepeatModeMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => context.InteractionManager.ExitRepeatMode()));
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
        if (state == InteractionState.RepeatMode)
            Enter();
        else if (_active)
            Exit();
    }

    private void Enter()
    {
        var context = Context!;

        var edgeAnchor = ScreenGeometry.RightEdgeAnchor(StopInsetLogical, UIConstants.ButtonRadius);
        var stopDef = CreateRepeatStopButtonDefinition(
            context.Settings.Repeat.StopButtonDwellMs, verticalOffset: 0);
        context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { stopDef }, edgeAnchor);

        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(context.Settings.Repeat.IntervalMs)
        };
        _timer.Tick += (_, _) =>
        {
            // No stable point (gaze lost / between fixations) → skip the tick. Never click a
            // stale point: a repeat aimed at nothing is worse than a missed beat.
            if (Context?.GazeEngine.StableCentroid is { } p)
                Context.InteractionManager.FireRepeatTick(p);
        };
        _timer.Start();
        _active = true;
    }

    private void Exit()
    {
        _timer?.Stop();
        _timer = null;
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
