namespace EyeCtrl.Core.Tests.Status;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Status;

/// <summary>
/// Wave B ticket B3's headless chime rule: the rising edge of <see cref="ControlStatus.Disconnected"/>,
/// gated by the setting and by <see cref="ControlState.NoTracker"/>. A deliberate pause alone, a lost
/// gaze alone and the mouse fallback stay silent; a disconnect during a pause still chimes, because a
/// pause does not hide a real tracker loss.
/// </summary>
public class StatusChimeGateTests
{
    private static ControlStatus Status(ControlState state, bool disconnected) =>
        new(state, disconnected);

    [Fact]
    public void ARisingDisconnectEdge_ChimesExactlyOncePerEpisode()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.Normal, false), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsTrue();

        // The status can be republished while it stays Disconnected (the state changes, the flag
        // does not). Only the EDGE is audible.
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.Normal, true), enabled: true)).IsFalse();
    }

    [Fact]
    public void AReconnect_ThenANewEpisode_ChimesAgain()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsTrue();
        Check.That(gate.ShouldChime(Status(ControlState.Normal, false), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsTrue();
    }

    [Fact]
    public void TrackingLostAlone_StaysSilent()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, false), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.Normal, false), enabled: true)).IsFalse();
    }

    [Fact]
    public void PausedAlone_StaysSilent_ButAPausedDisconnectStillChimes()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.Paused, false), enabled: true)).IsFalse();
        Check.WithCustomMessage("A deliberate pause is silent; a real disconnect in Pause is not.")
            .That(gate.ShouldChime(Status(ControlState.Paused, true), enabled: true)).IsTrue();
    }

    [Fact]
    public void NoTracker_StaysSilentEvenWhenTheProviderReportsDisconnected()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.NoTracker, true), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.NoTracker, false), enabled: true)).IsFalse();
        // A NoTracker episode must not leave a latched edge that fires when the state changes.
        Check.That(gate.ShouldChime(Status(ControlState.NoTracker, true), enabled: true)).IsFalse();
    }

    [Fact]
    public void SettingOff_StaysSilent_AndConsumesTheEdge()
    {
        var gate = new StatusChimeGate();

        Check.That(gate.ShouldChime(Status(ControlState.Normal, false), enabled: false)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: false)).IsFalse();

        // Turning the setting on DURING the same episode is not an edge and must stay silent.
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsFalse();

        // Only a reconnect and a new episode are audible.
        Check.That(gate.ShouldChime(Status(ControlState.Normal, false), enabled: true)).IsFalse();
        Check.That(gate.ShouldChime(Status(ControlState.TrackingLost, true), enabled: true)).IsTrue();
    }
}
