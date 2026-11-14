namespace EyeCtrl.App.Overlays;

using System;

/// <summary>Beats of the splash lifecycle (spec §6). Order: Idle → Entrance → Fill → Filled →
/// CatchUp → Title → Hold → Exit → Closed. <see cref="Filled"/> = the base course finished on its
/// own and the splash waits for readiness (the tail starts at readiness — spec §6.1, r4 #5).
/// Public because the xUnit theories parameterize on it (repo-wide convention: every enum a test
/// consumes — CoordinatorTickResult, RestartOutcome, … — is public).</summary>
public enum SplashBeat
{
    Idle,
    Entrance,
    Fill,
    Filled,
    CatchUp,
    Title,
    Hold,
    Exit,
    Closed,
}

/// <summary>
/// Pure decisions for the splash lifecycle (spec §6): the L16 catch-up rule (the catch-up only
/// ever SHORTENS what remains — never below base pace, r3 #1), beat transitions, the exit-request
/// acceptance window (any beat before the tail; ignored once CatchUp/Title/Hold/Exit/Closed —
/// r4 #5), and the dead-man / hard-deadline predicates. No WPF: the SplashWindow glue drives it,
/// unit tests exercise it headless.
/// </summary>
internal sealed class SplashExitController
{
    internal static readonly TimeSpan DeadManTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan HardExitDeadline = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CatchUpFloor = TimeSpan.FromSeconds(1);      // L16
    internal static readonly TimeSpan FillBaseCourse = TimeSpan.FromSeconds(3);    // §5 beat 3

    private SplashBeat _beat = SplashBeat.Idle;

    internal SplashBeat Beat => _beat;

    /// <summary>
    /// The L16 rule: the catch-up only ever shortens what remains — the remaining course at base
    /// pace when that is already ≤ the floor, the floor otherwise. No lower bound (r3 #1): a tiny
    /// remainder must land no later than it would have without the request.
    /// </summary>
    internal static TimeSpan ComputeCatchUpDuration(TimeSpan remainingBaseCourse) =>
        remainingBaseCourse < CatchUpFloor ? remainingBaseCourse : CatchUpFloor;

    /// <summary>
    /// The readiness request (<c>SplashWindow.BeginExit</c> → here). Idempotent, and accepted only
    /// from beats BEFORE the tail: Idle, Entrance, Fill, Filled. From CatchUp/Title/Hold/Exit/Closed
    /// it is ignored — the tail is already in flight (r4 #5). A zero remainder skips the catch-up
    /// storyboard entirely (the glue reads the returned duration).
    /// </summary>
    internal bool TryBeginExit(TimeSpan remainingBaseCourse, out TimeSpan catchUpDuration)
    {
        catchUpDuration = TimeSpan.Zero;
        if (_beat is SplashBeat.CatchUp or SplashBeat.Title or SplashBeat.Hold
            or SplashBeat.Exit or SplashBeat.Closed) return false;
        _beat = SplashBeat.CatchUp;
        catchUpDuration = ComputeCatchUpDuration(remainingBaseCourse);
        return true;
    }

    /// <summary>Moves the beat forward; a closed controller stays closed.</summary>
    internal void AdvanceTo(SplashBeat beat)
    {
        if (_beat is SplashBeat.Closed) return;
        _beat = beat;
    }

    internal void MarkClosed() => _beat = SplashBeat.Closed;

    /// <summary>Dead-man (L9): not Closed by 15 s → close now, whatever the beat.</summary>
    internal bool ShouldDeadManClose(TimeSpan elapsedSinceShow) =>
        _beat is not SplashBeat.Closed && elapsedSinceShow >= DeadManTimeout;

    /// <summary>Hard deadline (L9): BeginExit armed, not Closed within 3 s → close now.</summary>
    internal bool ShouldHardClose(TimeSpan elapsedSinceBeginExit) =>
        _beat is not SplashBeat.Closed && elapsedSinceBeginExit >= HardExitDeadline;
}
