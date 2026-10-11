namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;

/// <summary>
/// Fixup 08 item 2: the stale-capture retake, pure and headless. Consecutive plan steps never share
/// a placement (pinned), so a capture whose bytes equal the previous capture's bytes is the previous
/// frame: it is retaken up to <see cref="ProbeRingCaptureRetake.MaxRetakes"/> times, and if the copy
/// stands it is marked — never dropped silently. The harness prints these lines and the judgement
/// fails a stale capture in a judged row; the offline analyser applies the same rule to an archive.
/// </summary>
public class ProbeRingCaptureRetakeTests
{
    [Fact]
    public void FreshCapture_IsNotRetaken()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var waits = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () => bytes,
            previous: new byte[] { 9, 9, 9 },
            waitOneMoreFrame: () => waits++);

        Check.That(result.Retakes).IsEqualTo(0);
        Check.That(result.Stale).IsFalse();
        Check.That(result.Bytes).IsSameReferenceAs(bytes);
        Check.WithCustomMessage("A fresh capture must not pay for a wait.").That(waits).IsEqualTo(0);
    }

    /// <summary>
    /// Kills "retake disabled": the fake source returns the previous bytes first and fresh bytes
    /// after the wait, so a retake must happen exactly once — a no-op retake leaves the stale copy
    /// delivered and read as old pixels.
    /// </summary>
    [Fact]
    public void StaleFirstCapture_IsRetakenOnceUntilFresh()
    {
        var previous = new byte[] { 7, 7, 7, 7 };
        var fresh = new byte[] { 1, 1, 1, 1 };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () => ++captures == 1 ? previous : fresh,
            previous: previous,
            waitOneMoreFrame: () => waits++);

        Check.That(result.Retakes).IsEqualTo(1);
        Check.That(result.Stale).IsFalse();
        Check.That(result.Bytes).IsSameReferenceAs(fresh);
        Check.That(captures).IsEqualTo(2);
        Check.WithCustomMessage("The wait must happen before the retake, or the retake reads the same frame.")
            .That(waits).IsEqualTo(1);
    }

    [Fact]
    public void StillStaleAfterThreeRetakes_IsMarkedAndNotRetakenFurther()
    {
        var previous = new byte[] { 7, 7, 7 };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () =>
            {
                captures++;
                return previous;
            },
            previous: previous,
            waitOneMoreFrame: () => waits++);

        Check.That(result.Retakes).IsEqualTo(ProbeRingCaptureRetake.MaxRetakes);
        Check.WithCustomMessage("A copy that stands after three retakes must be marked, not dropped.")
            .That(result.Stale).IsTrue();
        Check.WithCustomMessage("The first capture plus exactly three retakes.")
            .That(captures).IsEqualTo(4);
        Check.That(waits).IsEqualTo(3);
    }

    [Fact]
    public void NoPreviousCapture_IsNeverStale()
    {
        var captures = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () =>
            {
                captures++;
                return new byte[] { 0 };
            },
            previous: null,
            waitOneMoreFrame: () => throw new InvalidOperationException("The first capture has nothing to be stale against."));

        Check.That(result.Retakes).IsEqualTo(0);
        Check.That(result.Stale).IsFalse();
        Check.That(captures).IsEqualTo(1);
    }

    /// <summary>
    /// Fixup 10 item 2: an intruded capture is retaken like a stale one, and the accepted capture's
    /// verdict is reported — a clean retake clears the mark, an intrusion that stands is marked with
    /// every observed verdict listed for the session lines.
    /// </summary>
    [Fact]
    public void AnIntrudedCapture_IsRetakenUntilClean()
    {
        var intruded = new ProbeRingIntrusion.Verdict(true, 5000, 1, 2, 40, 50);
        var clean = new ProbeRingIntrusion.Verdict(false, 0, 0, 0, 0, 0);
        var frames = new[] { new byte[] { 1 }, new byte[] { 2 } };
        var captures = 0;
        var waits = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () => frames[captures++],
            previous: null,
            waitOneMoreFrame: () => waits++,
            classifyIntrusion: bytes => bytes[0] == 1 ? intruded : clean);

        Check.That(result.Retakes).IsEqualTo(1);
        Check.That(result.Intruded).IsFalse();
        Check.That(result.Stale).IsFalse();
        Check.That(result.Intrusions).HasSize(1);
        Check.That(result.Bytes).IsSameReferenceAs(frames[1]);
        Check.WithCustomMessage("The retake must wait one more frame first.").That(waits).IsEqualTo(1);
    }

    [Fact]
    public void AnIntrusionThatStands_IsMarked_AndSharesTheStaleRetakeBudget()
    {
        var previous = new byte[] { 5, 5 };
        var bad = new ProbeRingIntrusion.Verdict(true, 9000, 0, 0, 9, 9);
        var captures = 0;

        var result = ProbeRingCaptureRetake.CaptureWithRetake(
            capture: () =>
            {
                captures++;
                return previous;   // stale AND intruded: both conditions at once
            },
            previous: previous,
            waitOneMoreFrame: () => { },
            classifyIntrusion: _ => bad);

        Check.WithCustomMessage("One shared budget: the capture plus three retakes, not three per condition.")
            .That(result.Retakes).IsEqualTo(ProbeRingCaptureRetake.MaxRetakes);
        Check.That(captures).IsEqualTo(4);
        Check.That(result.Stale).IsTrue();
        Check.That(result.Intruded).IsTrue();
        Check.WithCustomMessage("Every observed intruded capture is listed for the session lines.")
            .That(result.Intrusions).HasSize(4);
    }

    /// <summary>
    /// The session lines the harness prints: the pre-registered format
    /// (<c>R2 retake: step k (kind f) after N</c>) and the stale mark's line.
    /// </summary>
    [Fact]
    public void RetakeAndStaleLines_CarryTheStepKindFractionAndCount()
    {
        Check.That(ProbeRingCaptureRetake.RetakeLine(3, "sweep", 0.3, 1))
            .IsEqualTo("R2 retake: step 3 (sweep f=0.30) after 1");
        Check.WithCustomMessage("The stale line must name the step and the standing mark.")
            .That(ProbeRingCaptureRetake.StaleLine(7, "assertion", 0.25, 3))
            .Contains("R2 stale: step 7 (assertion f=0.25) after 3 retakes");
    }
}
