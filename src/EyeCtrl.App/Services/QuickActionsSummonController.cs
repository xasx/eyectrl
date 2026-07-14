namespace EyeCtrl.App.Services;

using System.Threading;
using EyeCtrl.Core.Models;

/// <summary>
/// Hot-edge gaze state machine (Idle ↔ Summoned) for the Quick Actions toolbar. When the user's
/// gaze dwells past the configured screen edge for <paramref name="summonThresholdMs"/>, the
/// controller summons the quick-actions grid; after <paramref name="dismissTimeoutMs"/> of
/// gaze-away (or on blink/calibration start) while Summoned, it dismisses.
///
/// Runs on the gaze thread. <see cref="State"/> is read through <see cref="Volatile.Read(ref int)"/>.
/// EventCoordinator (Task B6) marshals <see cref="SummonRequested"/>/<see cref="DismissRequested"/>
/// to the UI thread and drives the VM, mirroring ToolbarAutoHideController's
/// event → ToolbarVisibilityAdapter → dispatcher hop.
/// </summary>
public sealed class QuickActionsSummonController
{
    /// <summary>State of the summon state machine.</summary>
    public enum SummonState
    {
        /// <summary>Gaze is not dwelling past the edge; the quick-actions grid is hidden.</summary>
        Idle,
        /// <summary>The summon threshold was met; the quick-actions grid is (being) shown.</summary>
        Summoned
    }

    private const long Unseeded = long.MinValue;

    private string _edgeKey;
    private readonly double _screenW;
    private readonly double _screenH;
    private readonly int _summonThresholdUs;
    private readonly int _dismissTimeoutUs;
    private readonly Func<bool>? _calibrationActive;

    private int _state = (int)SummonState.Idle;
    // Written from both threads: Feed() on the gaze thread, ResetToIdleCore() (via
    // SetSuspended/UpdateEdge) on the UI thread. C# forbids `volatile long` (CS0677),
    // so accesses go through Volatile.Read/Volatile.Write — mirroring ToolbarAutoHideController.
    private long _runStartUs = Unseeded;
    private long _dismissStartUs = Unseeded;
    private volatile bool _suspended;

    /// <summary>Current state of the machine (thread-safe read).</summary>
    public SummonState State => (SummonState)Volatile.Read(ref _state);

    /// <summary>
    /// Raised on Idle→Summoned. EventCoordinator marshals this to the UI thread
    /// and sends QuickActionsToggledMessage (mirrors ToolbarAutoHideController's
    /// event → ToolbarVisibilityAdapter → dispatcher hop).
    /// </summary>
    public event Action? SummonRequested;

    /// <summary>
    /// Raised on Summoned→Idle via dismiss-timeout (gaze away for DismissTimeoutMs)
    /// or suppression (blink/calibration) while Summoned. Lets EventCoordinator hide
    /// the grid so the VM visibility and controller state stay in sync (H1 fix).
    /// NOT raised by <see cref="ResetToIdle"/>/<see cref="EnterSummoned"/> — those are
    /// explicit-state hooks the coordinator calls after it already drove the VM.
    /// </summary>
    public event Action? DismissRequested;

    /// <summary>
    /// Creates a summon controller for the given screen geometry and timing.
    /// </summary>
    /// <param name="edge">Docked edge to watch: "Left", "Right", "Top", or "Bottom" (case-insensitive).</param>
    /// <param name="screenW">Screen width in physical pixels.</param>
    /// <param name="screenH">Screen height in physical pixels.</param>
    /// <param name="summonThresholdMs">Dwell time past the edge before summoning.</param>
    /// <param name="dismissTimeoutMs">Gaze-away time while Summoned before dismissing.</param>
    /// <param name="calibrationActive">Optional gate; when it returns <c>true</c>, gaze is suppressed (no summon/dismiss).</param>
    public QuickActionsSummonController(
        string edge,
        double screenW,
        double screenH,
        int summonThresholdMs,
        int dismissTimeoutMs,
        Func<bool>? calibrationActive = null)
    {
        _edgeKey = edge.ToLowerInvariant();
        _screenW = screenW;
        _screenH = screenH;
        _summonThresholdUs = summonThresholdMs * 1000;
        _dismissTimeoutUs = dismissTimeoutMs * 1000;
        _calibrationActive = calibrationActive;
    }

    /// <summary>
    /// Feed a raw gaze sample. Signature matches ToolbarAutoHideController.Feed:
    /// validity lives inside <paramref name="sample"/>.IsValid, not a separate arg.
    /// </summary>
    /// <param name="sample">The gaze sample (coordinates in screen space, validity inside the struct).</param>
    /// <param name="timestampUs">Wall-clock timestamp in microseconds for threshold/timeout measurement.</param>
    public void Feed(GazePoint sample, long timestampUs)
    {
        if (_suspended) return;

        var pastEdge = IsPastEdge(sample);
        var shouldSuppress = (_calibrationActive?.Invoke() ?? false) || !sample.IsValid;

        if (State == SummonState.Idle)
        {
            if (pastEdge && !shouldSuppress)
            {
                var runStart = Volatile.Read(ref _runStartUs);
                if (runStart == Unseeded)
                {
                    runStart = timestampUs;
                    Volatile.Write(ref _runStartUs, runStart);
                }

                if (timestampUs - runStart >= _summonThresholdUs)
                {
                    Volatile.Write(ref _state, (int)SummonState.Summoned);
                    Volatile.Write(ref _runStartUs, Unseeded);
                    Volatile.Write(ref _dismissStartUs, Unseeded);
                    SummonRequested?.Invoke();
                }
            }
            else
            {
                Volatile.Write(ref _runStartUs, Unseeded);
            }
        }
        else // Summoned
        {
            if (shouldSuppress)
            {
                // Blink or calibration start while summoned → dismiss.
                ResetToIdleCore(notify: true);
                return;
            }

            // If gaze is inside screen (not past edge), start dismiss timer
            if (!pastEdge)
            {
                var dismissStart = Volatile.Read(ref _dismissStartUs);
                if (dismissStart == Unseeded)
                {
                    dismissStart = timestampUs;
                    Volatile.Write(ref _dismissStartUs, dismissStart);
                }

                if (timestampUs - dismissStart >= _dismissTimeoutUs)
                    ResetToIdleCore(notify: true);
            }
            else
            {
                Volatile.Write(ref _dismissStartUs, Unseeded);
            }
        }
    }

    private void ResetToIdleCore(bool notify)
    {
        Volatile.Write(ref _state, (int)SummonState.Idle);
        Volatile.Write(ref _runStartUs, Unseeded);
        Volatile.Write(ref _dismissStartUs, Unseeded);
        if (notify) DismissRequested?.Invoke();
    }

    private bool IsPastEdge(GazePoint p)
    {
        return _edgeKey switch
        {
            "left" => p.X < 0,
            "right" => p.X >= _screenW,
            "top" => p.Y < 0,
            "bottom" => p.Y >= _screenH,
            _ => false
        };
    }

    /// <summary>
    /// Suspends or resumes the controller. Suspending forces the machine to Idle and fires
    /// <see cref="DismissRequested"/> (notify:true) so the coordinator can hide the grid.
    /// While suspended, <see cref="Feed"/> is a no-op.
    /// </summary>
    /// <param name="suspended"><c>true</c> to suspend and force Idle; <c>false</c> to resume.</param>
    public void SetSuspended(bool suspended)
    {
        if (suspended == _suspended) return;
        _suspended = suspended;
        if (suspended) ResetToIdleCore(notify: true);
    }

    /// <summary>
    /// Explicit-state hook: force the machine to Summoned without firing
    /// <see cref="SummonRequested"/> (the coordinator calls this after it already showed the grid).
    /// </summary>
    public void EnterSummoned()
    {
        Volatile.Write(ref _state, (int)SummonState.Summoned);
        Volatile.Write(ref _runStartUs, Unseeded);
        Volatile.Write(ref _dismissStartUs, Unseeded);
    }

    /// <summary>
    /// Explicit-state hook: force the machine to Idle without firing
    /// <see cref="DismissRequested"/> (the coordinator calls this after it already hid the grid).
    /// </summary>
    public void ResetToIdle()
    {
        // Explicit-state hook — no DismissRequested (coordinator already drove the VM).
        ResetToIdleCore(notify: false);
    }

    /// <summary>
    /// Updates the watched edge. If currently Summoned, dismisses first (notify:true) so the
    /// coordinator can hide the grid before the new edge takes effect.
    /// </summary>
    /// <param name="edge">New edge: "Left", "Right", "Top", or "Bottom" (case-insensitive).</param>
    public void UpdateEdge(string edge)
    {
        if (State == SummonState.Summoned)
            ResetToIdleCore(notify: true);
        _edgeKey = edge.ToLowerInvariant();
    }
}
