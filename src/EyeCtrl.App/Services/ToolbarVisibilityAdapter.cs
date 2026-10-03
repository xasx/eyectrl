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

    // Edge-detect + event (spec B3 #5). _lastVisible seeds true because App.ShowOverlays
    // shows the toolbar directly at startup without passing through this adapter — the
    // adapter never sees that transition, and seeding false would emit a spurious "true"
    // on the first re-assert.
    private bool _lastVisible = true;

    // The strip's commanded state (spec H1): owned HERE, edge-guarded in SetPauseIndicator, and
    // read by the band show paths only to decide whether to re-raise the strip above the band.
    private bool _pauseIndicatorVisible;

    // The last command POSTED for the strip (issue #166 M8). Checked by the pre-check before
    // queuing another: skipping when this already equals the command is correct even with an
    // earlier command still in flight, because the queued pipeline then already ends in the
    // commanded state — comparing against the EXECUTED mirror instead would wrongly skip a real
    // transition (true→false arriving while the true is still queued would skip the false and
    // strand the strip shown; that exact sequence is Unpause_WhileBandVisible_HidesStrip).
    // No synchronisation needed because the only sender of StateChangedMessage marshals the
    // send onto the UI dispatcher (GazeEngineAdapter.OnStateChanged), so commands arrive
    // serialized on one thread (round-1 review N1 — the call-site count was the wrong reason).
    private bool _commandedPauseIndicator;

    /// <summary>
    /// Fires once per real show/hide transition, ON the UI thread (inside each dispatcher
    /// lambda, after the window calls). Re-asserts — SetPaused/ReassertVisibility call the
    /// hide paths repeatedly — are deduplicated. NOTE: not every driver funnels through the
    /// adapter (App.ShowOverlays shows the toolbar directly), so consumers must not treat
    /// silence as "hidden" (spec B3 #7).
    /// </summary>
    public event Action<bool>? VisibilityChanged;

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher, ToolbarViewModel toolbarVm)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _toolbarVm = toolbarVm;
    }

    private void OnVisibilityChanged(bool visible)
    {
        if (_lastVisible == visible) return;
        _lastVisible = visible;
        VisibilityChanged?.Invoke(visible);
    }

    public void SetPauseIndicator(bool visible)
    {
        // Pre-check on the CALLER's thread (issue #166 M8): StateChangedMessage fires per gaze
        // flip, and the edge guard used to run only inside the posted lambda — every flip queued
        // a dispatcher item just to return there, the exact cost class the no-change guard at
        // ToolbarAutoHideController.SetPaused documents as load-bearing. The authoritative write
        // stays inside the lambda, whose guard is DEFENSIVE ONLY: with commands arriving
        // serialized on one thread (the invariant the field comment states), the pre-check
        // refuses every redundant post and each queued lambda carries the opposite of the one
        // before it, so the guard is unreachable today (round-2 review N-1) — it costs nothing
        // and backstops the invariant if a second caller ever appears.
        if (_commandedPauseIndicator == visible) return;
        _commandedPauseIndicator = visible;
        _dispatcher.BeginInvoke(() =>
        {
            if (_pauseIndicatorVisible == visible) return; // StateChangedMessage fires per gaze flip
            _pauseIndicatorVisible = visible;
            if (visible)
            {
                _overlays.PauseIndicator.Show();
                _overlays.PauseIndicator.RaiseToTop();
            }
            else
            {
                _overlays.PauseIndicator.Hide();
            }
        });
    }

    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
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
        // The strip stays above a band shown after it (spec N2/H3): RaiseToTop is uncontested
        // for the whole slide — the slide animates a TranslateTransform (render transform), so
        // the band's HWND z-order never changes during it (review O6).
        if (_pauseIndicatorVisible)
            _overlays.PauseIndicator.RaiseToTop();
        OnVisibilityChanged(true);
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        // Deactivate dwell BEFORE the band starts leaving. The slide widens the window between
        // this and the actual Hide() from one dispatcher tick to the slide duration, which makes
        // this ordering more load-bearing than it was, not less.
        _toolbarVm.SetActive(false);
        _overlays.Toolbar.SlideOutThenHide(() =>
        {
            // The strip is deliberately NOT touched here: its visibility is commanded only
            // through SetPauseIndicator (spec H1) — a band leaving has nothing to say about it.
            //
            // The hide completes inside this callback (the instant short-circuit included), so
            // the transition event fires with it — the keyboard un-yields only after the band
            // has cleared, mirroring the show path's after-the-window-call ordering.
            OnVisibilityChanged(false);
        });
        // Gated on IsSliding — i.e. only when a slide actually starts — unlike ShowToolbar's
        // unconditional line. The gate keeps this line TRUTHFUL, not merely quiet: HideToolbar is
        // still reached on paths where nothing slides, because the toolbar is already hidden —
        // ReassertVisibility re-asserts the hide on every calibration-terminal state, and a
        // genuine pause change while hidden re-asserts it too (both in ToolbarAutoHideController:
        // ReassertVisibility and SetPaused). Logging those as "slide-out started" would corrupt
        // the one signal this logging exists to give.
        //
        // An earlier version of this comment justified the gate as flood control, because
        // SetPaused then had no no-change guard and re-asserted on EVERY interaction-state change —
        // a live run measured 129 re-asserts against 3 real hides, which is how that defect was
        // found. It was fixed 33 minutes later by SetPaused's no-change guard, so the flood is
        // gone and only the truthfulness argument above remains.
        //
        // ShowToolbar needs no gate: it is only ever called on a real summon.
        if (_overlays.Toolbar.IsSliding)
            Log.Information("Toolbar auto-hidden: slide-out started (paused={Paused})", paused);
    });

    public void ForceShow() => _dispatcher.BeginInvoke(() =>
    {
        // Instant, and it must stay instant: every caller — tray fallback, modal suspend-recover,
        // dock toggle — exists because the user needs the toolbar now. ShowAtRest also cancels any
        // in-flight slide and defuses its pending Hide().
        //
        // Logged, unlike the other two seams' silent paths: a ForceShow that arrives while the
        // controller believes Hidden (the settings suspend-recover — and, until de68be31 reversed
        // it, the paused-trap guard) is the one transition whose absence looked identical to a
        // dead summon in the 2026-09-26 field log. One line closes that hole.
        Log.Information("Toolbar force-shown (ShowAtRest)");
        _overlays.Toolbar.ShowAtRest();
        // Same raise as ShowToolbar (spec N2/H3): a commanded strip stays above the band. There is
        // no slide to stay uncontested through — ShowAtRest is instant — so the raise simply
        // follows the show.
        if (_pauseIndicatorVisible)
            _overlays.PauseIndicator.RaiseToTop();
        _toolbarVm.SetActive(true);
        OnVisibilityChanged(true);
    });

    public void PositionIndicator(string edge) => _dispatcher.BeginInvoke(() => _overlays.PauseIndicator.SetEdge(edge));

    public void SetActive(bool active) => _dispatcher.BeginInvoke(() => _toolbarVm.SetActive(active));
}
