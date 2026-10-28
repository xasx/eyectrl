namespace EyeCtrl.Core.Status;

using EyeCtrl.Core.Enums;

/// <summary>
/// Wave B ticket B3's headless chime rule: decides whether one published
/// <see cref="ControlStatus"/> is the RISING EDGE of a tracker disconnect the owner should hear.
/// <para>
/// The rule is stateful on purpose. A status message is published on every change, and
/// <see cref="ControlStatus.Disconnected"/> stays true through several of them
/// (the state moves between TrackingLost and Normal while the flag does not), so the gate remembers
/// the previous alert condition and fires once per episode. The edge is consumed even while the
/// setting is off: turning the chime on during an ongoing disconnect is not a rising edge.
/// </para>
/// <para>
/// <b>Blocked seam.</b> The owner wants the chime for a lost tracker AND a blocked OS input, but
/// <see cref="ControlState.Blocked"/> is reserved and nothing raises it yet (B1). When it does, add
/// it to <see cref="IsAlerting"/> — one case — and the same edge logic covers it.
/// </para>
/// <para>
/// Thread safety: like the status model, the live service receives messages on the UI thread; the
/// gate itself is not synchronized and is not meant to be called from several threads at once.
/// </para>
/// </summary>
public sealed class StatusChimeGate
{
    private bool _alerting;

    /// <summary>
    /// Observes one status and answers whether it should play the chime now.
    /// </summary>
    /// <param name="status">The committed status carried by the change message.</param>
    /// <param name="enabled"><c>StatusChimeEnabled</c> at the time of the message.</param>
    /// <returns><c>true</c> for the first alerting status of a new episode.</returns>
    public bool ShouldChime(ControlStatus status, bool enabled)
    {
        // Update the remembered edge BEFORE the gates: a rising edge that arrives while the setting
        // is off (or on the mouse fallback) is consumed, not replayed later.
        bool rising = IsAlerting(status) && !_alerting;
        _alerting = IsAlerting(status);

        return rising && enabled && status.State != ControlState.NoTracker;
    }

    /// <summary>
    /// Whether this status is one the chime cares about. A later Blocked rising edge joins here.
    /// </summary>
    private static bool IsAlerting(ControlStatus status) => status.Disconnected;
}
