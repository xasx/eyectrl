namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// Real <see cref="IToolbarVisibility"/> over <see cref="IOverlayManager"/>. Marshals every
/// call onto the WPF dispatcher (the controller calls from the gaze thread).
/// </summary>
public sealed class ToolbarVisibilityAdapter : IToolbarVisibility
{
    private readonly IOverlayManager _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly ToolbarViewModel _toolbarVm;

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher, ToolbarViewModel toolbarVm)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _toolbarVm = toolbarVm;
    }

    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
        // Indicator first, so it is not underneath the incoming band.
        _overlays.PauseIndicator.Hide();
        // Clock attached, then Show, then arm — arming is SYNCHRONOUS and deliberately not
        // deferred to the slide's completion: a summon already costs 400 ms of dwell, and the
        // targets are at their correct resting coordinates throughout, so up to 200 ms where a
        // button is drawn short of its target is the cheaper side of the trade. See the spec's
        // open question 1. SetActive follows Show for the same reason HideToolbar's precedes
        // Hide: the window reaches its safe configuration before dwell state changes.
        _overlays.Toolbar.ShowThenSlideIn();
        _toolbarVm.SetActive(true);
        // The slide is otherwise entirely silent, which made two live runs unverifiable: nothing
        // in the log said whether a transition had fired, so the only evidence available was
        // watching a 200 ms animation. IsSliding is read AFTER the call on purpose — it is what
        // separates a real animation from the instant short-circuit taken when the toolbar is
        // floating (zero slide vector) or already on screen.
        Log.Information("Toolbar summoned: slide-in {Mode}",
            _overlays.Toolbar.IsSliding ? "animating" : "instant");
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        // Deactivate dwell BEFORE the band starts leaving. The slide widens the window between
        // this and the actual Hide() from one dispatcher tick to the slide duration, which makes
        // this ordering more load-bearing than it was, not less.
        _toolbarVm.SetActive(false);
        _overlays.Toolbar.SlideOutThenHide(() =>
        {
            // After the band has cleared, so the indicator reads as a handover rather than a swap.
            if (paused)
            {
                _overlays.PauseIndicator.Show();
            }
            else
            {
                _overlays.PauseIndicator.Hide();
            }
        });
        // See the note in ShowToolbar. "instant" here also covers the already-hidden
        // short-circuit, which SetPaused and ReassertVisibility both reach.
        Log.Information("Toolbar auto-hidden: slide-out {Mode} (paused={Paused})",
            _overlays.Toolbar.IsSliding ? "animating" : "instant", paused);
    });

    public void ForceShow() => _dispatcher.BeginInvoke(() =>
    {
        // Instant, and it must stay instant: every caller — tray fallback, modal suspend-recover,
        // dock toggle — exists because the user needs the toolbar now. ShowAtRest also cancels any
        // in-flight slide and defuses its pending Hide().
        _overlays.Toolbar.ShowAtRest();
        _overlays.PauseIndicator.Hide();
        _toolbarVm.SetActive(true);
    });

    public void PositionIndicator(string edge) => _dispatcher.BeginInvoke(() => _overlays.PauseIndicator.SetEdge(edge));

    public void SetActive(bool active) => _dispatcher.BeginInvoke(() => _toolbarVm.SetActive(active));
}
