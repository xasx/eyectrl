namespace EyeCtrl.Core.Status;

using EyeCtrl.Core.Enums;

/// <summary>
/// The published control status: the current <see cref="ControlState"/> plus the separate
/// <see cref="Disconnected"/> flag.
/// <para>
/// The two are deliberately separate. The state answers "what should the user see?" — the strip
/// appears after 3 s without valid gaze. The flag answers "has the tracker really gone?" — it needs
/// <c>IsConnected</c> to be false CONTINUOUSLY for more than 10 s, because the 09-24 walk-away storm
/// flapped the Tobii call every ~0.64 s (0.53 s disconnected, 0.1 s connected). B3's chime uses the
/// flag so a walk-away never chimes. A <see cref="ControlState.TrackingLost"/> state with
/// <see cref="Disconnected"/> false is therefore normal and expected.
/// </para>
/// </summary>
/// <param name="State">
/// The current control state, by the documented priority. Deliberately a mirror, not the strip's
/// visibility rule: B2a/B2b's paused strip hides on <c>InteractionManager.CurrentState</c> directly,
/// never on this value (B1 fixup-01 / review F2). The status follows the same transition; reading
/// the state machine is what keeps the strip's rule independent of this model's publication path.
/// </param>
/// <param name="Disconnected">
/// <c>true</c> only when <c>IGazeProvider.IsConnected</c> has been observed false continuously for
/// more than 10 s. Clears on the first connected observation.
/// </param>
public readonly record struct ControlStatus(ControlState State, bool Disconnected);
