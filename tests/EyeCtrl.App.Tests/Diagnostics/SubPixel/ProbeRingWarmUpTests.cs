namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;

/// <summary>
/// Fixup 10 item 1, tightened by fixup 11 R1: the warm-up gate, pure and headless. Run 3's first
/// frame was black for about a second; the reference captured then anchors every row, so the
/// warm-up now waits for a frame that passes the positive control, is intrusion-free AND is settled
/// (byte-identical to the previous capture) — at most 3 s, naming the condition that never held on
/// timeout. The harness then starts the retake chain empty.
///
/// <para>
/// Frames are byte-tagged: <c>{1}</c> fails the control, <c>{2}</c> passes it but is intruded,
/// <c>{3}</c>/<c>{4}</c> are clean control-passing frames (distinct bytes, so consecutive ones are
/// not settled).
/// </para>
/// </summary>
public class ProbeRingWarmUpTests
{
    private static readonly byte[] ControlFail = [1];
    private static readonly byte[] Intruded = [2];
    private static readonly byte[] Clean = [3];
    private static readonly byte[] CleanOther = [4];

    private static ProbeRingWarmUpFrame Classify(byte[] bytes) =>
        new(ControlPasses: bytes[0] != 1, IntrusionFree: bytes[0] != 2);

    [Fact]
    public void StopsAtTheFirstSettledCleanRingFrame_AndReportsTheCount()
    {
        var frames = new[] { ControlFail, Intruded, Clean, Clean };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () => frames[captures++],
            classify: Classify,
            waitOneMoreFrame: () => waits++,
            elapsedMilliseconds: () => 100L * captures);

        Check.WithCustomMessage("The warm-up must stop at the first control-passing, clean, settled frame.")
            .That(result.Passed).IsTrue();
        Check.WithCustomMessage($"It must wait through the control-less and intruded frames; got {result.Captures}.")
            .That(result.Captures).IsEqualTo(4);
        Check.That(result.LastBytes).IsSameReferenceAs(frames[3]);
        Check.WithCustomMessage("Every capture is preceded by a present+flush wait.").That(waits).IsEqualTo(4);
        Check.That(result.SawControlPass).IsTrue();
        Check.That(result.SawIntrusionFree).IsTrue();
        Check.That(result.SawSettled).IsTrue();
    }

    /// <summary>
    /// Kills "warm-up accepts an unsettled frame": a clean control-passing frame is not enough on
    /// its own; the second identical one is what proves the window finished rendering.
    /// </summary>
    [Fact]
    public void AFirstCleanFrame_IsNotEnough_ItWaitsOneMoreForSettle()
    {
        var frames = new[] { Clean, Clean };
        var captures = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () => frames[captures++],
            classify: Classify,
            waitOneMoreFrame: () => { },
            elapsedMilliseconds: () => 100L * captures);

        Check.That(result.Passed).IsTrue();
        Check.WithCustomMessage(
                "The first clean frame has no predecessor, so the settle condition needs the second; got "
                + $"{result.Captures} captures.")
            .That(result.Captures).IsEqualTo(2);
    }

    /// <summary>
    /// Kills "warm-up accepts an intruded frame": an intruded frame that passes the control and
    /// settles is still never accepted, and the timeout names the intrusion-free condition.
    /// </summary>
    [Fact]
    public void AnIntrudedFrame_ThatSettles_IsNeverAccepted()
    {
        var captures = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () =>
            {
                captures++;
                return Intruded;
            },
            classify: Classify,
            waitOneMoreFrame: () => { },
            elapsedMilliseconds: () => 100L * captures);

        Check.WithCustomMessage("An overlay over the reference is never a usable warm-up frame.")
            .That(result.Passed).IsFalse();
        Check.WithCustomMessage("The control passed, but intrusion-free never did.")
            .That(result.SawControlPass).IsTrue();
        Check.That(result.SawIntrusionFree).IsFalse();
        Check.WithCustomMessage("The copy settled from the second capture on.")
            .That(result.SawSettled).IsTrue();
    }

    [Fact]
    public void ACleanButNeverSettlingStream_TimesOut_AndNamesSettle()
    {
        var captures = 0;
        var frames = new[] { Clean, CleanOther };

        var result = ProbeRingWarmUp.Wait(
            capture: () =>
            {
                captures++;
                return frames[(captures - 1) % 2];
            },
            classify: Classify,
            waitOneMoreFrame: () => { },
            elapsedMilliseconds: () => 100L * captures);

        Check.WithCustomMessage("A stream of differing clean frames never settles, so the warm-up must fail.")
            .That(result.Passed).IsFalse();
        Check.That(result.SawControlPass).IsTrue();
        Check.That(result.SawIntrusionFree).IsTrue();
        Check.That(result.SawSettled).IsFalse();

        Check.WithCustomMessage("The failure must name the settle condition as never held.")
            .That(ProbeRingWarmUp.FailureLine(
                result.Captures, ProbeRingWarmUp.TimeoutMs, (10, 10, 10), false, false, false))
            .Contains("settled");
    }

    [Fact]
    public void TimesOut_AndReportsTheCountElapsedAndLastBytes()
    {
        var captures = 0;
        var waits = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () =>
            {
                captures++;
                return ControlFail;
            },
            classify: Classify,
            waitOneMoreFrame: () => waits++,
            elapsedMilliseconds: () => 1000L * captures);

        Check.WithCustomMessage("A warm-up that never sees the control pass must fail, not loop forever or pass.")
            .That(result.Passed).IsFalse();
        Check.WithCustomMessage("The clock reads 1000 ms per capture, so the 3000 ms bound expires on the third.")
            .That(result.Captures).IsEqualTo(3);
        Check.That(result.ElapsedMs).IsEqualTo(3000);
        Check.WithCustomMessage("The failure keeps the last capture, so the message can name its colour.")
            .That(result.LastBytes).IsSameReferenceAs(ControlFail);
        Check.That(waits).IsEqualTo(3);
        Check.That(result.SawControlPass).IsFalse();
    }

    [Fact]
    public void Lines_CarryTheCountsTheMeanColourAndTheNeverHeldConditions()
    {
        Check.That(ProbeRingWarmUp.Line(7, 412))
            .IsEqualTo("R2 warm-up: first ring frame after 7 captures, 412 ms");

        var failure = ProbeRingWarmUp.FailureLine(4, 3000, (15, 15, 15), false, false, false);
        Check.WithCustomMessage("The failure line must name the capture's mean colour (run 3 read (15, 15, 15)).")
            .That(failure).Contains("(15, 15, 15)");
        Check.That(failure).Contains("4 captures");
        Check.WithCustomMessage("It must name every condition that never held.")
            .That(failure).Contains("the positive control");
        Check.That(failure).Contains("intrusion-free");
        Check.That(failure).Contains("settled");
    }

    [Fact]
    public void MeanColour_AveragesThePixels()
    {
        // Two pixels, BGRA: (0,0,0,0) and (B=10, G=20, R=30, A=255).
        var bgra = new byte[] { 0, 0, 0, 0, 10, 20, 30, 255 };

        Check.That(ProbeRingWarmUp.MeanColour(bgra)).IsEqualTo((R: 15, G: 10, B: 5));
    }
}
