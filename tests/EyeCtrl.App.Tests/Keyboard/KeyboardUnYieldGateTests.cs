namespace EyeCtrl.App.Tests.Keyboard;

using EyeCtrl.App.Overlays;

/// <summary>
/// The pure gaze-off clock behind C1's un-yield (owner decision 24.10): 300 ms of continuous real
/// samples outside the keyboard window opens the gate; a sample inside the window resets it, and a
/// silence longer than 150 ms restarts it (a blink/tracker dropout is not evidence the gaze is
/// away). Every feed below is a continuous stream (50 ms steps) unless the test is about a gap —
/// a spaced-out feed would itself restart the run. No window is constructed here.
/// </summary>
public class KeyboardUnYieldGateTests
{
    private const long OffUs = KeyboardUnYieldGate.OffThresholdUs;
    private const long ContinuousStepUs = 50_000;

    /// <summary>Feeds one outside sample every 50 ms from <paramref name="fromUs"/> through
    /// <paramref name="toUs"/> inclusive, and returns the last answer.</summary>
    private static bool FeedOutside(KeyboardUnYieldGate gate, long fromUs, long toUs)
    {
        var answer = false;
        for (var t = fromUs; t <= toUs; t += ContinuousStepUs)
            answer = gate.Update(true, t);
        return answer;
    }

    [Fact]
    public void OpensAt300MsOff_NotAt299Ms()
    {
        var gate = new KeyboardUnYieldGate();

        Check.That(FeedOutside(gate, 0, 250_000)).IsFalse();
        Check.That(gate.Update(true, OffUs - 1_000)).IsFalse();      // 299 ms off
        Check.That(gate.Update(true, OffUs)).IsTrue();               // 300 ms
    }

    [Fact]
    public void AnInsideSample_ResetsTheOffRun()
    {
        var gate = new KeyboardUnYieldGate();

        Check.That(FeedOutside(gate, 0, 200_000)).IsFalse();
        Check.That(gate.Update(false, 250_000)).IsFalse();           // inside: the run restarts
        Check.That(FeedOutside(gate, 300_000, 550_000)).IsFalse();
        Check.That(gate.Update(true, 599_000)).IsFalse();            // 299 ms since the restart
        Check.That(gate.Update(true, 600_000)).IsTrue();             // 300 ms since the restart
    }

    [Fact]
    public void ASilenceLongerThan150Ms_RestartsTheOffRun()
    {
        var gate = new KeyboardUnYieldGate();

        Check.That(gate.Update(true, 0)).IsFalse();
        // A 400 ms silence: the elapsed time must NOT be credited as "off".
        Check.That(gate.Update(true, 400_000)).IsFalse();
        Check.That(FeedOutside(gate, 400_000, 650_000)).IsFalse();
        Check.That(gate.Update(true, 699_000)).IsFalse();            // 299 ms since the restart
        Check.That(gate.Update(true, 700_000)).IsTrue();             // 300 ms since the restart
    }

    [Fact]
    public void ASilenceOfExactly150Ms_IsStillContinuous()
    {
        // The bound is the same as KeyboardViewModel.MaxContinuousSampleGapUs: exactly 150 ms is
        // credited as held gaze, and only a longer gap restarts.
        var gate = new KeyboardUnYieldGate();

        Check.That(gate.Update(true, 0)).IsFalse();
        Check.That(gate.Update(true, 150_000)).IsFalse();
        Check.That(gate.Update(true, 300_000)).IsTrue();
    }

    [Fact]
    public void Reset_StartsTheOffRunOver()
    {
        var gate = new KeyboardUnYieldGate();

        Check.That(FeedOutside(gate, 0, OffUs - 1_000)).IsFalse();

        gate.Reset();

        Check.That(FeedOutside(gate, OffUs, 550_000)).IsFalse();
        Check.That(gate.Update(true, 2 * OffUs - 1_000)).IsFalse();   // the pre-reset run is gone
        Check.That(gate.Update(true, 2 * OffUs)).IsTrue();
    }
}
