namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;

/// <summary>
/// Fixup 10 item 1: the warm-up gate, pure and headless. Run 3's first frame was black for about a
/// second and the black reference then threw in the 21-row scan; the harness waits for a capture
/// that passes the positive control (≤ 3 s) before the plan loop, then starts the retake chain
/// empty (<c>previousBgra = null</c>). Kills "warm-up skipped" (via the gate source pin), "previous
/// Bgra not reset" (same) and "the timeout never failing" (here).
/// </summary>
public class ProbeRingWarmUpTests
{
    [Fact]
    public void StopsAtTheFirstRingFrame_AndReportsTheCount()
    {
        var black = new byte[] { 0 };
        var ring = new byte[] { 1 };
        var frames = new[] { black, black, ring };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () => frames[captures++],
            isRingFrame: bytes => ReferenceEquals(bytes, ring),
            waitOneMoreFrame: () => waits++,
            elapsedMilliseconds: () => 1000L * captures);

        Check.WithCustomMessage("The warm-up must stop at the first ring frame.")
            .That(result.Passed).IsTrue();
        Check.WithCustomMessage($"It must report the captures it took; got {result.Captures}.")
            .That(result.Captures).IsEqualTo(3);
        Check.That(result.LastBytes).IsSameReferenceAs(ring);
        Check.WithCustomMessage("Every capture is preceded by a present+flush wait.").That(waits).IsEqualTo(3);
    }

    [Fact]
    public void TimesOut_AndReportsTheCountElapsedAndLastBytes()
    {
        var black = new byte[] { 7 };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () =>
            {
                captures++;
                return black;
            },
            isRingFrame: _ => false,
            waitOneMoreFrame: () => waits++,
            elapsedMilliseconds: () => 1000L * captures);

        Check.WithCustomMessage("A warm-up that never sees the ring must fail, not loop forever or pass.")
            .That(result.Passed).IsFalse();
        Check.WithCustomMessage("The clock reads 1000 ms per capture, so the 3000 ms bound expires on the third.")
            .That(result.Captures).IsEqualTo(3);
        Check.That(result.ElapsedMs).IsEqualTo(3000);
        Check.WithCustomMessage("The failure keeps the last capture, so the message can name its colour.")
            .That(result.LastBytes).IsSameReferenceAs(black);
        Check.That(waits).IsEqualTo(3);
    }

    [Fact]
    public void AFirstCaptureThatPasses_TakesOneWaitOneCapture()
    {
        var ring = new byte[] { 1 };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingWarmUp.Wait(
            capture: () =>
            {
                captures++;
                return ring;
            },
            isRingFrame: _ => true,
            waitOneMoreFrame: () => waits++,
            elapsedMilliseconds: () => 0);

        Check.That(result.Passed).IsTrue();
        Check.That(result.Captures).IsEqualTo(1);
        Check.That(waits).IsEqualTo(1);
    }

    [Fact]
    public void Lines_CarryTheCountsAndTheMeanColour()
    {
        Check.That(ProbeRingWarmUp.Line(7, 412))
            .IsEqualTo("R2 warm-up: first ring frame after 7 captures, 412 ms");
        Check.WithCustomMessage("The failure line must name the capture's mean colour (run 3 read (15, 15, 15)).")
            .That(ProbeRingWarmUp.FailureLine(4, 3000, (15, 15, 15)))
            .Contains("(15, 15, 15)");
        Check.That(ProbeRingWarmUp.FailureLine(4, 3000, (15, 15, 15))).Contains("4 captures");
    }

    [Fact]
    public void MeanColour_AveragesThePixels()
    {
        // Two pixels, BGRA: (0,0,0,0) and (B=10, G=20, R=30, A=255).
        var bgra = new byte[] { 0, 0, 0, 0, 10, 20, 30, 255 };

        Check.That(ProbeRingWarmUp.MeanColour(bgra)).IsEqualTo((R: 15, G: 10, B: 5));
    }
}
