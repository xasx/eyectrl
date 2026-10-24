namespace EyeCtrl.App.Tests.Keyboard;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

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

    [Fact]
    public void ABackwardsClock_RestartsTheOffRun()
    {
        // Review 01 F4 / mutant M9: a timestamp earlier than the last one is a discontinuity, not
        // evidence of a continuous off-run.
        var gate = new KeyboardUnYieldGate();

        Check.That(gate.Update(true, 1_000_000)).IsFalse();
        Check.That(gate.Update(true, 1_200_000)).IsFalse();          // 200 ms off
        Check.That(gate.Update(true, 900_000)).IsFalse();            // clock went backwards
        Check.That(FeedOutside(gate, 900_000, 1_150_000)).IsFalse();
        Check.That(gate.Update(true, 1_199_000)).IsFalse();          // 299 ms since the restart
        Check.That(gate.Update(true, 1_200_000)).IsTrue();           // 300 ms since the restart
    }

    // ---- the keep-clear rect: the current window UNION the band it would move into (review 01 F1) ----

    [Theory]
    [InlineData("bottom", 1.0)]
    [InlineData("top", 1.0)]
    [InlineData("bottom", 2.0)]   // the owner's 200 % display
    [InlineData("top", 2.0)]
    public void KeepClearRect_ExtendsTowardTheDockedEdge_ByTheYieldGivenBack(string dockEdge, double pixelsPerDip)
    {
        var current = new PhysicalRect(100, 100, 500, 200);
        var applied = dockEdge == "top" ? 132 : -132;   // a full toolbar yield on that edge
        var expectedDeltaPx = 132 * pixelsPerDip;

        var keepClear = KeyboardUnYieldGate.KeepClearRect(current, dockEdge, applied, 0, pixelsPerDip);

        Check.That(keepClear.X).IsEqualTo(current.X);
        Check.That(keepClear.Width).IsEqualTo(current.Width);
        Check.That(keepClear.Height).IsEqualTo(current.Height + expectedDeltaPx);
        Check.WithCustomMessage("the union must extend toward the screen edge the keyboard docks to")
            .That(keepClear.Y).IsEqualTo(dockEdge == "top" ? current.Y - expectedDeltaPx : current.Y);
    }

    [Theory]
    [InlineData("bottom", 1.0)]
    [InlineData("top", 1.0)]
    [InlineData("bottom", 2.0)]
    [InlineData("top", 2.0)]
    public void KeepClearRect_WithASmallerRemainingToolbar_IsPartial(string dockEdge, double pixelsPerDip)
    {
        var current = new PhysicalRect(100, 100, 500, 200);
        var applied = dockEdge == "top" ? 132 : -132;
        var remaining = dockEdge == "top" ? 60 : -60;
        var expectedDeltaPx = 72 * pixelsPerDip;   // 132 - 60

        var keepClear = KeyboardUnYieldGate.KeepClearRect(current, dockEdge, applied, remaining, pixelsPerDip);

        Check.That(keepClear.Height).IsEqualTo(current.Height + expectedDeltaPx);
        Check.That(keepClear.Y).IsEqualTo(dockEdge == "top" ? current.Y - expectedDeltaPx : current.Y);
    }

    [Theory]
    [InlineData(132.0)]    // the toolbar is still there at the same extent: no move
    [InlineData(200.0)]    // it grew without a Yield placement: still no move
    public void KeepClearRect_WhenNoUnYieldApplies_IsTheWindowRect(double currentYieldDip)
    {
        var current = new PhysicalRect(100, 100, 500, 200);

        var keepClear = KeyboardUnYieldGate.KeepClearRect(current, "bottom", -132, -currentYieldDip, 2.0);

        Check.That(keepClear).IsEqualTo(current);
    }
}
