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
    private readonly double _screenW;
    private readonly double _screenH;
    private PhysicalRect _toolbarRect;
    private string _edge; // "Top"/"Bottom"/"Left"/"Right" — settable (changes on DockedEdge edit)
    private string _edgeKey; // lower-cased _edge, normalized once so IsPastDockedEdge doesn't allocate per sample
    private volatile bool _enabled; // mutable: re-evaluated when IsDocked changes at runtime

    // ── Runtime state ──
    // _suspended / _paused / _enabled / _state are written on the UI thread (SetSuspended / SetPaused /
    // UpdateEnabled / ResetToVisible) and read in Feed() (gaze thread) → marked volatile, mirroring
    // EventCoordinator's _keyboardVisible / _settingsOpen. The two anchor fields below are written by UI-thread resets
    // (SetSuspended/UpdateEnabled) and by Feed on the gaze thread; long is wider than 32 bits so C#
    // forbids `volatile long` (CS0677) — accesses go through Volatile.Read/Volatile.Write instead,
    // which provides the same acquire/release semantics the comment intended.
    // _visibility is written once by Attach() before the GazeSampled subscription begins
    // (happens-before guarantee), so it need not be volatile; marking it would be purely defensive.
    //
    // _toolbarRect, _edge, and _edgeKey are written on the UI thread (UpdateToolbarRect/UpdateEdge)
    // and read on the gaze thread; they're not marked volatile. A torn read of the 32-byte rect or a
    // stale edge would at worst cause a one-sample-stale decision (acceptable — the same one-frame-stale
    // rationale EventCoordinator uses for its _keyboardVisible/_settingsOpen flags).
    //
    // Sentinel discipline: long.MinValue means "unseeded / not counting". Do NOT use 0 — real
    // tracker timestamps carry an arbitrary large epoch (TobiiStreamEngineProvider monotonic µs),
    // and the tests start at t=0, so 0 is a legitimate value that must not be overloaded with
    // "no active run" semantics (that bug caused both test failures and instant-hide-at-launch).
    public enum State { Visible, Hidden }
    private const long Unseeded = long.MinValue;
    private volatile State _state = State.Visible;
    private long _hideAnchorUs = Unseeded;   // Visible: timestamp of the start of the current off-toolbar run (or last on-toolbar sample)
    private long _summonRunUs = Unseeded;     // Hidden:  start of the current sustained past-edge run
    private volatile bool _suspended;
    private volatile bool _paused;

    // ── Output seam ──
    private IToolbarVisibility? _visibility;

    public ToolbarAutoHideController(bool enabled, string edge, double screenW, double screenH,
        PhysicalRect toolbarRect, int hideDelayMs, int summonDelayMs, double offScreenMarginPx,
        Func<bool>? calibrationActive = null,
        Func<PhysicalPoint, PhysicalPoint>? toScreen = null)
    {
        _enabled = enabled;
        _edge = edge;
        _edgeKey = edge.ToLowerInvariant();
        _screenW = screenW;
        _screenH = screenH;
        _toolbarRect = toolbarRect;
        _hideDelayUs = hideDelayMs * 1000L;
        _summonDelayUs = summonDelayMs * 1000L;
        _offScreenMarginPx = offScreenMarginPx;
        _calibrationActive = calibrationActive;
        _toScreen = toScreen;
    }

    /// <summary>Binds the output seam. Real wiring passes an OverlayManager adapter; tests pass a fake.</summary>
    public void Attach(IToolbarVisibility visibility)
    {
        _visibility = visibility;
        visibility.PositionIndicator(_edge);
    }

    // ── Configuration setters (called from EventCoordinator on the UI thread) ──

    public void SetPaused(bool paused)
    {
        _paused = paused;
        // If the toolbar is already HIDDEN, no hide/show transition will fire to update the edge
        // bar — so react to the pause change here. Re-asserting HideToolbar keeps the toolbar hidden
        // (a no-op) while toggling the indicator: pause-while-hidden shows the bar, resume hides it.
        // Without this the user can be paused + hidden + no feedback (spec Goal #4 violation).
        if (_state == State.Hidden && _enabled && !_suspended)
            _visibility?.HideToolbar(paused);
    }

    public void SetSuspended(bool suspended)
    {
        if (suspended == _suspended) return;
        _suspended = suspended;
        if (suspended)
        {
            // Recover from hidden so the user isn't stranded while a modal is up.
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

    /// <summary>Refreshes the cached toolbar rect (call on the UI thread after layout/dock changes).</summary>
    public void UpdateToolbarRect(PhysicalRect rect) => _toolbarRect = rect;

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

        switch (_state)
        {
            case State.Visible:
            {
                // The rect is screen geometry captured from the live window, but the sample arrives
                // pre-calibration (GazeSampled fires before the calibration transform in
                // GazeEngine.OnGazePoint). Map into screen space for THIS test only — and only for
                // valid samples: an invalid one pays no mapper call. IsPastDockedEdge below keeps
                // raw coordinates: off-screen gaze is outside the transform's fitted domain.
                if (!sample.IsValid)
                {
                    // Blink: ignored, does NOT reset the hide countdown (per spec).
                }
                else
                {
                    var probe = _toScreen is null
                        ? new PhysicalPoint(sample.X, sample.Y)
                        : _toScreen(new PhysicalPoint(sample.X, sample.Y));
                    if (_toolbarRect.Contains(probe.X, probe.Y))
                    {
                        // Gaze on toolbar — anchor to "now" so the hide countdown measures time since
                        // the LAST on-toolbar sample (a later off-toolbar run resumes the count from here).
                        Volatile.Write(ref _hideAnchorUs, timestampUs);
                    }
                    else
                    {
                        // Off-toolbar run. Seed the anchor on the first such sample, then measure elapsed.
                        var anchor = Volatile.Read(ref _hideAnchorUs);
                        if (anchor == Unseeded)
                        {
                            anchor = timestampUs;
                            Volatile.Write(ref _hideAnchorUs, anchor);
                        }
                        if (timestampUs - anchor >= _hideDelayUs)
                        {
                            _state = State.Hidden;
                            Volatile.Write(ref _summonRunUs, Unseeded);
                            _visibility.HideToolbar(_paused);
                        }
                    }
                }
                break;
            }

            case State.Hidden:
                if (IsPastDockedEdge(sample))
                {
                    var runStart = Volatile.Read(ref _summonRunUs);
                    if (runStart == Unseeded)
                    {
                        runStart = timestampUs;
                        Volatile.Write(ref _summonRunUs, runStart);
                    }
                    if (timestampUs - runStart >= _summonDelayUs)
                    {
                        _state = State.Visible;
                        Volatile.Write(ref _summonRunUs, Unseeded);
                        Volatile.Write(ref _hideAnchorUs, timestampUs); // start the post-summon hide countdown fresh
                        _visibility.ShowToolbar();
                    }
                }
                else
                {
                    Volatile.Write(ref _summonRunUs, Unseeded); // condition broken — reset summon countdown
                }
                break;
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
        return _edgeKey switch
        {
            "top"    => s.Y < -_offScreenMarginPx,
            "bottom" => s.Y > _screenH + _offScreenMarginPx,
            "left"   => s.X < -_offScreenMarginPx,
            "right"  => s.X > _screenW + _offScreenMarginPx,
            _        => false, // unknown/malformed edge never summons (defensive)
        };
    }

    // ── Test-only helpers (used by ToolbarAutoHideControllerTests) ──
    // (InternalsVisibleTo is not declared from EyeCtrl.App; these are public test hooks instead.
    //  Acceptable for a controller whose entire purpose is testable state transitions.)

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
