namespace EyeCtrl.Core.Enums;

/// <summary>
/// The user's control status — "do you have control right now?" (Wave B ticket B1).
/// <para>
/// Exactly one of these is current at a time. The priority when several conditions hold is
/// <see cref="NoTracker"/> &gt; <see cref="TrackingLost"/> &gt; <see cref="Paused"/> &gt;
/// <see cref="Normal"/>: having no tracker is the most fundamental fact, and a lost stream is more
/// actionable than the fact that the user also paused.
/// </para>
/// </summary>
public enum ControlState
{
    /// <summary>An eye tracker is the provider and delivering valid samples; not paused.</summary>
    Normal,

    /// <summary>The interaction state machine is paused (the user asked for it).</summary>
    Paused,

    /// <summary>
    /// An eye tracker is the provider but no valid gaze sample has arrived for at least
    /// <c>ControlStatusModel.TrackingLostAfterMs</c> (3 s). It clears on the first valid sample.
    /// </summary>
    TrackingLost,

    /// <summary>
    /// The provider is the mouse fallback — there is no eye tracker at all (no device at startup, or
    /// the auto-detect probe fell back to the mouse).
    /// </summary>
    NoTracker,

    /// <summary>
    /// Reserved. OS input blocking (UAC / the lock screen) is unproven — no log evidence yet — so B1
    /// defines the state but NO code path raises it. It waits for a deliberate UAC/lock check in a
    /// sitting.
    /// </summary>
    Blocked
}
