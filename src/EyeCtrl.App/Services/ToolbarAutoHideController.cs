namespace EyeCtrl.App.Services;

using System.Threading;
using EyeCtrl.Core.Models;

/// <summary>
/// Auto-hides the docked toolbar when gaze isn't on it, and summons it back by looking past the
/// docked screen edge. Runs a two-state hysteresis machine on the gaze thread against cached
/// geometry; dispatches visibility changes to the UI thread only on transitions.
///
/// See docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md.
/// </summary>
public sealed class ToolbarAutoHideController
{
    // ── Tunables (defaults from the spec) ──
    private readonly long _hideDelayUs;     // run of "gaze off toolbar" before hiding
    private readonly long _summonDelayUs;   // run of "gaze past docked edge" before summoning
    private readonly double _offScreenMarginPx;
    private readonly Func<bool>? _calibrationActive;
    private readonly Func<PhysicalPoint, PhysicalPoint>? _toScreen;

    // ── Geometry (cached, physical px) ──
    /// <summary>
    /// Screen bounds and summon rectangle as one immutable value, swapped by a single reference
    /// write.
    /// </summary>
    /// <remarks>
    /// The screen size used to be two <c>readonly</c> doubles, so a resolution change could never
    /// be applied — and the summon rectangle, the escape hatch a user reaches for when anything
    /// else has gone wrong, was measured against a screen that no longer existed.
    /// <para>
    /// They are one record because they must change TOGETHER. Refreshing the rect and the bounds
    /// independently is the failure this fix exists to avoid a subtler form of: the summon region
    /// would be expressed in the new geometry while <see cref="IsPastDockedEdge"/> still tested
    /// against the old screen, so the toolbar could become unreachable with no bug anywhere in the
    /// logic. The surrounding comment's tolerance for a one-sample-stale rect is about a rect that
    /// is merely OLD; it was never a tolerance for a rect and a screen size that disagree.
    /// </para>
    /// </remarks>
    private sealed record Geometry(double ScreenW, double ScreenH, PhysicalRect ToolbarRect);

    private volatile Geometry _geometry;
    private string _edge; // "Top"/"Bottom"/"Left"/"Right" — settable (changes on DockedEdge edit)
    private string _edgeKey; // lower-cased _edge, normalized once so IsPastDockedEdge doesn't allocate per sample
    private volatile bool _enabled; // mutable: re-evaluated when IsDocked changes at runtime

    // ── Runtime state ──
    // _suspended / _paused / _enabled / _state are written on the UI thread (SetSuspended / SetPaused /
    // UpdateEnabled / ResetToVisible) and read in Feed() (gaze thread) → marked volatile, mirroring
    // the ModalInteractionState.KeyboardVisible / .SettingsOpen flags (read in
    // ToolbarLifecycleCoordinator.SetupGazeRouting's early-out). The two anchor fields below are written by UI-thread resets
    // (SetSuspended/UpdateEnabled) and by Feed on the gaze thread; long is wider than 32 bits so C#
    // forbids `volatile long` (CS0677) — accesses go through Volatile.Read/Volatile.Write instead,
    // which provides the same acquire/release semantics the comment intended.
    // _visibility is written once by Attach() before the GazeSampled subscription begins
    // (happens-before guarantee), so it need not be volatile; marking it would be purely defensive.
    //
    // _edge and _edgeKey are written on the UI thread (UpdateEdge) and read on the gaze thread;
    // they're not marked volatile. A stale edge would at worst cause a one-sample-stale decision
    // (acceptable — the same one-frame-stale rationale ModalInteractionState uses for its
    // KeyboardVisible/SettingsOpen flags).
    //
    // The rect is NO LONGER among them. It moved into the volatile _geometry record alongside the
    // screen bounds, because the two must change together: this comment's tolerance was for a rect
    // that is merely OLD, and it was never a tolerance for a rect and a screen size that disagree —
    // that combination can leave the toolbar unsummonable, which is a lockout rather than a stale
    // frame. Torn reads of the rect are gone as a side effect: a record swap is one reference write.
    //
    // Sentinel discipline: long.MinValue means "unseeded / not counting". Do NOT use 0 — real
    // tracker timestamps carry an arbitrary large epoch (TobiiStreamEngineProvider monotonic µs),
    // and the tests start at t=0, so 0 is a legitimate value that must not be overloaded with
    // "no active run" semantics (that bug caused both test failures and instant-hide-at-launch).
    public enum State { Visible, Hidden }
    private const long Unseeded = long.MinValue;
    // Longest stretch without a valid sample (invalid frames, or no frames at all) the Hidden-case
    // summon run survives; measured between the valid samples on either side of it. 400 ms keeps
    // the longest invalid stretch in the user's 443 s recording (302 ms) without being anything
    // like a walk-away: longer means the user was not there for the run, so the next valid sample
    // starts a fresh one.
    private const long SummonGapCapUs = 400_000;
    private volatile State _state = State.Visible;
    private long _hideAnchorUs = Unseeded;   // Visible: timestamp of the start of the current off-toolbar run (or last on-toolbar sample)
    private long _summonRunUs = Unseeded;     // Hidden:  start of the current sustained past-edge run
    private volatile bool _suspended;
    private volatile bool _paused;

    // Diagnostic-transition trackers (gaze thread only, Feed is their only writer). See _diagnostics.
    private bool _lastSampleOnToolbar;
    private bool _lastSampleValid = true;

    // Summon-start lines are persist-gated like the reset lines: a gaze flickering across the
    // threshold starts and resets a run every few samples, and an ungated start line would log at
    // up to half the sample rate. The flag marks the CURRENT run as logged; cleared on run start,
    // on the threshold transition and by nothing else.
    private bool _summonStartLogged;

    // The summon margin band (past the screen edge, short of the summon threshold — see
    // IsInsideSummonMarginBand). Entry is logged only after the band has held for the same 100 ms
    // the run lines use, so flicker at the threshold logs nothing; invalid samples freeze the band
    // state (a blink while hovering must not read as "left the band"). Both gaze-thread-only.
    private long? _marginBandSinceUs;
    private bool _marginBandLogged;

    // Timestamp of the last VALID sample seen while Hidden; null on entry to Hidden. The Hidden
    // case returns early on invalid samples (spec §State machine: "Invalid samples reset neither
    // timer"), so this is what measures the gap that ends a summon run: on the next valid sample, a
    // stretch longer than SummonGapCapUs means the user was away — invalid frames, or no frames at
    // all, because a walk-away's TIMED_OUT storm sends neither — and the run is killed rather than
    // resumed. Gaze-thread only.
    private long? _lastValidHiddenUs;

    // ── Output seam ──
    private IToolbarVisibility? _visibility;

    // Optional diagnostic sink (production wiring: Serilog; tests: null). The 2026-09-26 field
    // failure — a hide with no summon for 25 s of edge gaze — was undiagnosable from the log
    // because the summon run's start/reset and every state write were silent. These lines exist
    // to make one real run decisive. Every call site is edge-triggered or persist-gated, never
    // per sample: state writes (SetPaused/SetSuspended, change-only), sample-validity flips,
    // toolbar-rect enter/leave, summon-run start/reset/threshold gated to runs of ≥100 ms, and
    // the margin-band enter/leave gated to holds of ≥100 ms.
    private Action<string>? _diagnostics;

    public ToolbarAutoHideController(bool enabled, string edge, double screenW, double screenH,
        PhysicalRect toolbarRect, int hideDelayMs, int summonDelayMs, double offScreenMarginPx,
        Func<bool>? calibrationActive = null,
        Func<PhysicalPoint, PhysicalPoint>? toScreen = null)
    {
        _enabled = enabled;
        _edge = edge;
        _edgeKey = edge.ToLowerInvariant();
        _geometry = new Geometry(screenW, screenH, toolbarRect);
        _hideDelayUs = hideDelayMs * 1000L;
        _summonDelayUs = summonDelayMs * 1000L;
        _offScreenMarginPx = offScreenMarginPx;
        _calibrationActive = calibrationActive;
        _toScreen = toScreen;
    }

    /// <summary>Binds the diagnostic sink. Call once during wiring, before the first sample.</summary>
    internal void AttachDiagnostics(Action<string> sink) => _diagnostics = sink;

    private void Diag(string message) => _diagnostics?.Invoke(message);

    /// <summary>Binds the output seam. Real wiring passes an OverlayManager adapter; tests pass a fake.</summary>
    public void Attach(IToolbarVisibility visibility)
    {
        _visibility = visibility;
        visibility.PositionIndicator(_edge);
    }

    /// <summary>Whether the controller is currently suspended. Test-visible read of <see cref="_suspended"/>
    /// (mirrors <see cref="QuickActionsSummonController.IsSuspended"/>).</summary>
    internal bool IsSuspended => _suspended;

    // ── Configuration setters (called from ToolbarLifecycleCoordinator on the UI thread) ──

    public void SetPaused(bool paused)
    {
        // No-change early-out, mirroring SetSuspended below. Load-bearing, not tidiness:
        // ToolbarLifecycleCoordinator mirrors EVERY StateChangedMessage into this method, and
        // that message fires on each Fixation<->FreeLook flip — several times a second during
        // ordinary gaze. Without the guard, a hidden toolbar re-asserted the hide below at roughly
        // 1.4 Hz forever, each time costing a dispatcher hop, a DwellDetector.ResetAllState (via
        // the adapter's SetActive(false)) and a Hide() on an already-hidden window — all on the
        // UI thread that renders the gaze cursor and services dwell. A 2026-09-07 live run
        // measured 129 such re-asserts against 3 real hides.
        if (paused == _paused) return;
        _paused = paused;
        Diag($"paused={paused} (state={_state})");

        // Paused hides like any other state (owner decision 2026-09-26, after that day's
        // instrumented run; it reverses the 2026-09-24 trap guard): the toolbar auto-hides while
        // paused, and the way back is the edge summon the pause-indicator strip marks, then dwell
        // Play. The 2026-09-24 log (the stranding the trap guard answered) shows 10 paused summons
        // and 11 paused auto-hides, and no resume followed any of them — the summon worked. No
        // summon → Play → resume has been observed on this code yet. The tray ForceShow remains
        // the non-gaze escape hatch.
        //
        // If the band is already HIDDEN, no hide/show transition will fire to carry the pause
        // change into the visibility seam — re-assert the hide, keeping the seam and toolbar dwell
        // consistent with the Hidden state (dwell re-deactivated, window re-hidden). HideToolbar
        // does NOT touch the pause indicator under the strip-neutral contract (spec H1): the
        // paused argument only reaches the adapter's log, and the strip's own mirror in
        // ToolbarLifecycleCoordinator commands it on the same StateChangedMessage.
        if (_state == State.Hidden && _enabled && !_suspended)
            _visibility?.HideToolbar(paused);
    }

    public void SetSuspended(bool suspended)
    {
        if (suspended == _suspended) return;
        _suspended = suspended;
        Diag($"suspended={suspended} (state={_state})");
        if (suspended)
        {
            // Recover from hidden so the user isn't stranded while the Settings window is up (the
            // keyboard no longer suspends auto-hide — its escape is the edge-bar Close; see the
            // 2026-09-10 keyboard spec).
            if (_state == State.Hidden && _enabled)
                _visibility?.ForceShow();
            _state = State.Visible;
            Volatile.Write(ref _hideAnchorUs, Unseeded);  // unsuspend starts a fresh hide countdown
            Volatile.Write(ref _summonRunUs, Unseeded);
        }
        else
        {
            // Just unsuspended: reset the hide countdown so it doesn't instantly hide.
            Volatile.Write(ref _hideAnchorUs, Unseeded);
        }
    }

    /// <summary>
    /// Re-asserts the controller's current visibility + dwell activity. Call when an external
    /// dwell suppressor (calibration) ends, so re-activation respects a hide that occurred while
    /// the suppressor held Feed in early-return. Without this, a terminal calibration state
    /// (Active/Failed/Idle) blind-activates dwell even when the toolbar has since auto-hidden —
    /// leaving Close dwellable with no toolbar visible.
    /// </summary>
    /// <remarks>
    /// State-aware counterpart to a blind <see cref="IToolbarVisibility.SetActive"/>(true). When the
    /// controller is Hidden (and enabled, not suspended), re-asserts the hide so dwell stays
    /// deactivated AND the toolbar stays hidden; otherwise activates dwell. This is what makes the
    /// auto-hide controller the single source of truth for re-activation instead of the calibration
    /// handler at <see cref="EventCoordinator"/>.
    /// </remarks>
    public void ReassertVisibility()
    {
        if (_visibility == null) return;
        if (_state == State.Hidden && _enabled && !_suspended)
            _visibility.HideToolbar(_paused);   // re-asserts hide AND SetActive(false)
        else
            _visibility.SetActive(true);
    }

    /// <summary>Refreshes the cached toolbar rect (call on the UI thread after layout/dock changes).</summary>
    public void UpdateToolbarRect(PhysicalRect rect)
    {
        var g = _geometry;
        _geometry = new Geometry(g.ScreenW, g.ScreenH, rect);
    }

    /// <summary>
    /// Re-publishes the screen bounds and the summon rectangle together after a display,
    /// resolution or scaling change.
    /// </summary>
    /// <remarks>
    /// One call rather than a bounds setter plus <see cref="UpdateToolbarRect"/>, because the two
    /// values are only meaningful with respect to each other: a rect from the new screen judged
    /// against the old bounds (or the reverse) can leave the toolbar permanently unsummonable.
    /// Making the pair inseparable in the API is what stops a future caller updating one and
    /// forgetting the other.
    /// </remarks>
    public void UpdateGeometry(double screenW, double screenH, PhysicalRect toolbarRect)
    {
        if (screenW <= 0 || screenH <= 0) return;   // transient 0x0 during a mode switch
        _geometry = new Geometry(screenW, screenH, toolbarRect);
    }

    /// <summary>Refreshes the active docked edge (call when DockedEdge changes in settings).</summary>
    public void UpdateEdge(string edge)
    {
        _edge = edge;
        _edgeKey = edge.ToLowerInvariant();
    }

    /// <summary>
    /// Re-evaluates enablement (call when IsDocked changes at runtime). If transitioning to
    /// disabled, force-shows the toolbar and resets to Visible so a now-floating toolbar doesn't
    /// auto-hide. Idempotent on no-op transitions.
    /// </summary>
    public void UpdateEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled && _visibility != null)
        {
            _visibility.ForceShow();
            _state = State.Visible;
            Volatile.Write(ref _hideAnchorUs, Unseeded);
            Volatile.Write(ref _summonRunUs, Unseeded);
        }
    }

    // ── The pure decision (gaze thread) ──

    /// <summary>
    /// Feeds one raw gaze sample and applies any state transition. Returns the resulting state.
    /// Pure with respect to external effects — only touches <see cref="_visibility"/> on transitions.
    /// </summary>
    public State Feed(GazePoint sample, long timestampUs)
    {
        if (!_enabled || _suspended || _visibility == null)
            return _state;
        if (_calibrationActive?.Invoke() == true)
            return _state; // suspended during calibration

        // Sample validity is tracked in BOTH states, edge-triggered. While Hidden the summon
        // decision reads raw coordinates, so "never attempted" must be distinguishable from "gaze
        // was lost" and from "hovering short of the threshold" — the silent six minutes of
        // 2026-09-26 23:19–23:25 (Hidden, no line at all) is exactly what Visible-only tracking
        // could not see.
        if (!sample.IsValid)
        {
            if (_lastSampleValid)
                Diag("gaze samples went INVALID (hide/summon tests skip these)");
            _lastSampleValid = false;
        }
        else if (!_lastSampleValid)
        {
            Diag("gaze samples valid again");
            _lastSampleValid = true;
        }

        switch (_state)
        {
            case State.Visible:
            {
                // Paused runs the same countdown as unpaused (owner decision 2026-09-26, reversing
                // the 2026-09-24 trap guard): the band hides under paused gaze exactly as it does
                // unpaused, and the Hidden case below summons it back with no pause gate. The way
                // back while paused: past-edge gaze (the pause-indicator strip marks that edge),
                // then dwell the Play toggle.

                // The rect is screen geometry captured from the live window, but the sample arrives
                // pre-calibration (GazeSampled fires before the calibration transform in
                // GazeEngine.OnGazePoint). Map into screen space for THIS test only — and only for
                // valid samples: an invalid one pays no mapper call. IsPastDockedEdge below keeps
                // raw coordinates: off-screen gaze is outside the transform's fitted domain.
                if (!sample.IsValid)
                {
                    // Blink: ignored, does NOT reset the hide countdown (per spec). The validity
                    // flip already logged above.
                }
                else
                {
                    var probe = _toScreen is null
                        ? new PhysicalPoint(sample.X, sample.Y)
                        : _toScreen(new PhysicalPoint(sample.X, sample.Y));
                    if (_geometry.ToolbarRect.Contains(probe.X, probe.Y))
                    {
                        if (!_lastSampleOnToolbar)
                            Diag("gaze entered the toolbar rect");
                        _lastSampleOnToolbar = true;

                        // Gaze on toolbar — anchor to "now" so the hide countdown measures time since
                        // the LAST on-toolbar sample (a later off-toolbar run resumes the count from here).
                        Volatile.Write(ref _hideAnchorUs, timestampUs);
                    }
                    else
                    {
                        if (_lastSampleOnToolbar)
                            Diag("gaze left the toolbar rect");
                        _lastSampleOnToolbar = false;

                        // Off-toolbar run. Seed the anchor on the first such sample, then measure elapsed.
                        var anchor = Volatile.Read(ref _hideAnchorUs);
                        if (anchor == Unseeded)
                        {
                            anchor = timestampUs;
                            Volatile.Write(ref _hideAnchorUs, anchor);
                        }
                        if (timestampUs - anchor >= _hideDelayUs)
                        {
                            Diag($"hide fired after {(timestampUs - anchor) / 1000} ms off-toolbar");
                            _state = State.Hidden;
                            Volatile.Write(ref _summonRunUs, Unseeded);
                            _lastValidHiddenUs = null; // the first Hidden sample measures its own gap
                            _visibility.HideToolbar(_paused);
                        }
                    }
                }
                break;
            }

            case State.Hidden:
            {
                // A blink is not a broken summon run (spec §State machine: "Invalid samples reset
                // neither timer", and this is now the only gaze way out of Paused, so forgiving
                // beats precise): skip the run test and let the run keep counting through the gap.
                if (!sample.IsValid)
                    return _state;

                // The gap that DOES end a run is measured between the valid samples on either side
                // of it, so it covers invalid frames and frame silence alike (a walk-away ends in a
                // TIMED_OUT storm, where no callbacks arrive at all). A run that straddles a gap
                // longer than SummonGapCapUs is dead; this sample seeds a fresh one below.
                if (_lastValidHiddenUs is long lastValid
                    && timestampUs - lastValid > SummonGapCapUs
                    && Volatile.Read(ref _summonRunUs) != Unseeded)
                {
                    Volatile.Write(ref _summonRunUs, Unseeded);
                    _summonStartLogged = false;
                }
                _lastValidHiddenUs = timestampUs;

                // The summon margin band: past the screen edge but short of the summon threshold
                // (top: -50 ≤ raw y < 0). This is where a user summaring too shallowly sits — the
                // state the log previously could not distinguish from not trying at all. Entry is
                // logged only after the band has held 100 ms, exit only if entry logged, and
                // invalid samples freeze the band state (a blink while hovering is not a leave).
                if (sample.IsValid)
                {
                    var inBand = IsInsideSummonMarginBand(sample);
                    if (inBand)
                    {
                        _marginBandSinceUs ??= timestampUs;
                        if (!_marginBandLogged && timestampUs - _marginBandSinceUs.Value >= 100_000)
                        {
                            _marginBandLogged = true;
                            Diag($"raw gaze inside the summon margin band for 100+ ms (raw y={sample.Y:F0}, x={sample.X:F0}, margin={_offScreenMarginPx:F0}) — short of the summon threshold");
                        }
                    }
                    else if (_marginBandSinceUs is not null)
                    {
                        if (_marginBandLogged)
                            Diag("raw gaze left the summon margin band");
                        _marginBandLogged = false;
                        _marginBandSinceUs = null;
                    }
                }

                if (IsPastDockedEdge(sample))
                {
                    var runStart = Volatile.Read(ref _summonRunUs);
                    if (runStart == Unseeded)
                    {
                        runStart = timestampUs;
                        Volatile.Write(ref _summonRunUs, runStart);
                        _summonStartLogged = false;
                    }
                    if (timestampUs - runStart >= _summonDelayUs)
                    {
                        _state = State.Visible;
                        Volatile.Write(ref _summonRunUs, Unseeded);
                        Volatile.Write(ref _hideAnchorUs, timestampUs); // start the post-summon hide countdown fresh
                        _summonStartLogged = false;
                        _marginBandLogged = false;
                        _marginBandSinceUs = null;
                        Diag("summon threshold reached");
                        _visibility.ShowToolbar();
                    }
                    else if (!_summonStartLogged && timestampUs - runStart >= 100_000)
                    {
                        // Gated to runs of ≥100 ms exactly like the reset line below: a flicker
                        // across the threshold starts a run per sample-pair, and an ungated start
                        // line would log at up to half the sample rate. (2cc6745c's commit message
                        // wrongly claimed this gate already covered the start line.)
                        _summonStartLogged = true;
                        Diag($"summon run started (raw y={sample.Y:F0}, x={sample.X:F0}, margin={_offScreenMarginPx:F0})");
                    }
                }
                else
                {
                    // Condition broken — reset the summon countdown. Only a run that lasted ≥100 ms
                    // is worth a line; together with the gated start line, threshold flicker is
                    // silent and a real attempt leaves exactly two lines (started + reset). The
                    // sample is guaranteed valid here (invalid samples returned above), so this
                    // line can never print the provider's invalid sentinel (raw y=0, x=0) as a
                    // position — the misleading "jump to the top-left corner" the r1 review named.
                    var runStart = Volatile.Read(ref _summonRunUs);
                    if (runStart != Unseeded)
                    {
                        Volatile.Write(ref _summonRunUs, Unseeded);
                        if (timestampUs - runStart >= 100_000)
                            Diag($"summon run reset after {(timestampUs - runStart) / 1000} ms (raw y={sample.Y:F0}, x={sample.X:F0})");
                    }
                }
                break;
            }
        }

        return _state;
    }

    // Deliberately on raw tracker coordinates while the rect test in Feed maps through the
    // calibration transform: off-screen gaze lies outside the domain the transform was fit to, and
    // extrapolating there is less trustworthy than not mapping at all.
    //
    // The residual is worth naming rather than leaving implicit: the summon threshold and the rect
    // edge are then measured in different spaces, so the point where a summon triggers sits a
    // calibration-offset away from where the toolbar visually ends. Small in practice — Load rejects
    // any transform that deviates far from identity — but it is the same class of mismatch Fix 4
    // removed from the rect test, not an absence of one.
    private bool IsPastDockedEdge(GazePoint s)
    {
        if (!s.IsValid) return false; // invalid samples never summon (per spec)

        // One read of the geometry, so both branches of the switch and any future multi-axis test
        // are judged against the same screen. Two reads could straddle a display change.
        var g = _geometry;
        return _edgeKey switch
        {
            "top"    => s.Y < -_offScreenMarginPx,
            "bottom" => s.Y > g.ScreenH + _offScreenMarginPx,
            "left"   => s.X < -_offScreenMarginPx,
            "right"  => s.X > g.ScreenW + _offScreenMarginPx,
            _        => false, // unknown/malformed edge never summons (defensive)
        };
    }

    // Same raw-coordinate domain and one-geometry-read discipline as IsPastDockedEdge: the band
    // between the screen edge and the summon threshold (top: -50 ≤ raw y < 0). Only the Hidden
    // case consults it, for the persist-gated margin-band line.
    private bool IsInsideSummonMarginBand(GazePoint s)
    {
        if (!s.IsValid) return false;
        var g = _geometry;
        return _edgeKey switch
        {
            "top"    => s.Y < 0 && s.Y >= -_offScreenMarginPx,
            "bottom" => s.Y > g.ScreenH && s.Y <= g.ScreenH + _offScreenMarginPx,
            "left"   => s.X < 0 && s.X >= -_offScreenMarginPx,
            "right"  => s.X > g.ScreenW && s.X <= g.ScreenW + _offScreenMarginPx,
            _        => false,
        };
    }

    // ── Test-only helpers (used by ToolbarAutoHideControllerTests) ──
    // (InternalsVisibleTo("EyeCtrl.App.Tests") IS declared from EyeCtrl.App now, so internal hooks
    //  are available — see QuickActionsSummonController.IsSuspended. These pre-existing helpers
    //  remain public for back-compat with the tests that drive them. Acceptable for a controller
    //  whose entire purpose is testable state transitions.)

    public void HideForTest(IToolbarVisibility vis)
    {
        _state = State.Hidden;
        vis.HideToolbar(_paused);
    }

    /// <summary>
    /// Test/tray-fallback hook: force the state machine to Visible and reset both run anchors,
    /// so the toolbar doesn't immediately re-hide after being force-shown.
    /// </summary>
    public void ResetToVisible()
    {
        _state = State.Visible;
        Volatile.Write(ref _hideAnchorUs, Unseeded);
        Volatile.Write(ref _summonRunUs, Unseeded);
    }

    // Expose state name for any future assertion diagnostics.
    public string StateName => _state.ToString();
}
