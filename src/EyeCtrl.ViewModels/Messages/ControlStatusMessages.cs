namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Enums;

/// <summary>
/// Sent by <c>ControlStatusService</c> whenever the control status changes (Wave B ticket B1) — and
/// once with the initial status when the service starts, so a surface that subscribed during startup
/// still sees it.
/// <para>
/// State answers "what should the user see?" (the strip, B2b). <see cref="Disconnected"/> answers
/// "has the tracker really gone?" and is the flag B3's chime uses: it turns true only after
/// <c>IsConnected</c> has been false CONTINUOUSLY for more than 10 s, so the 09-24 walk-away storm —
/// 0.53 s disconnected, 0.1 s connected, repeated — never sets it. A <c>TrackingLost</c> message
/// with <see cref="Disconnected"/> false is normal: the eyes-left-the-screen case is not a chime.
/// </para>
/// <para>
/// Sent on the UI thread in the running app (the service posts through the dispatcher). The payload
/// is the whole status, so a recipient never has to pair it with a separately read current value.
/// </para>
/// <para>
/// <b>The paused strip does not key off <see cref="State"/>.</b> B2a/B2b's paused strip hides on
/// <c>InteractionManager.CurrentState</c> directly (B1 fixup-01 / review F2): the status is a
/// mirror for the strip states that have no direct source (Lost, NoTracker) and for the chime's
/// <see cref="Disconnected"/> flag, never the pause visibility rule.
/// </para>
/// </summary>
/// <param name="State">The committed control state, by the documented priority.</param>
/// <param name="Disconnected">True only for a continuous tracker disconnect of more than 10 s.</param>
public record ControlStatusChangedMessage(ControlState State, bool Disconnected);
