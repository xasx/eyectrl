namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class GapInterpolatorTests
{
    // Minimal stub — passes valid gaze through, returns (X,Y) as PhysicalPoint
    private sealed class PassThroughFilter : IGazeFilter
    {
        public PhysicalPoint Filter(GazePoint g) => new(g.X, g.Y);
        public void Reset() { }
    }

    // Same, but records resets — the only externally visible difference between the long-gap branch and
    // a short hold now that both report in-gap.
    private sealed class ResetCountingFilter : IGazeFilter
    {
        public int ResetCount { get; private set; }
        public PhysicalPoint Filter(GazePoint g) => new(g.X, g.Y);
        public void Reset() => ResetCount++;
    }

    private static GazePoint Valid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: true);

    private static GazePoint Invalid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: false);

    [Fact]
    public void Process_ValidSample_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Valid(100, 200, 0));
        Check.That(isInGap).IsFalse();
    }

    [Fact]
    public void Process_ValidSample_PositionMatchesRaw()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (pos, _) = gi.Process(Valid(100, 200, 0));
        Check.That(pos.X).IsEqualTo(100);
        Check.That(pos.Y).IsEqualTo(200);
    }

    [Fact]
    public void Process_ShortInvalidGap_IsInGapTrue()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));                 // establish last valid
        var (_, isInGap) = gi.Process(Invalid(0, 0, 50_000));  // 50ms < 150ms
        Check.That(isInGap).IsTrue();
    }

    [Fact]
    public void Process_ShortInvalidGap_HoldsLastValidPosition()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        var (pos, _) = gi.Process(Invalid(0, 0, 50_000));
        Check.That(pos.X).IsEqualTo(100);
        Check.That(pos.Y).IsEqualTo(200);
    }

    [Fact]
    public void Process_LongInvalidGap_IsInGapTrue()
    {
        // Was IsInGapFalse: resetting the filter starts a new sequence, but the position handed back is
        // still the held one, and the flag's sole consumer is a suppression gate. Reporting false here
        // left a one-sample phantom-fixation hole — the held point reached FixationDetector at distance
        // zero. Invariant now: any sample returning a held position is in-gap.
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        // 200ms >= 150ms -> long gap, treat as new sequence
        var (_, isInGap) = gi.Process(Invalid(0, 0, 200_000));
        Check.That(isInGap).IsTrue();
    }

    [Fact]
    public void Process_ResumeAfterShortGap_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 50_000));     // short gap
        var (_, isInGap) = gi.Process(Valid(105, 205, 100_000));  // resume
        Check.That(isInGap).IsFalse();
    }

    [Fact]
    public void Process_NoValidSampleYet_InvalidSample_IsInGapFalse()
    {
        // No prior valid sample — can't be "in gap"
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Invalid(0, 0, 0));
        Check.That(isInGap).IsFalse();
    }

    [Fact]
    public void Process_NoValidSampleYet_ManyInvalidSamples_AllIsInGapFalse()
    {
        // "Never seen data" must stay distinct from "sequence broken by a long gap": the tail fix
        // must not make a cold start look like a gap.
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        for (int i = 0; i < 10; i++)
            Check.That(gi.Process(Invalid(0, 0, i * 20_000)).IsInGap).IsFalse();
    }

    [Fact]
    public void Process_AfterLongGap_RemainingInvalidSamples_IsInGapTrue()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 200_000));  // long gap — breaks the sequence

        // Every remaining invalid sample of the same eyes-closed period stays in-gap, so the frozen
        // position is not fed to the fixation detector at distance zero.
        for (int i = 1; i <= 10; i++)
        {
            var (pos, isInGap) = gi.Process(Invalid(0, 0, 200_000 + (i * 20_000)));
            Check.That(isInGap).IsTrue();
            Check.That(pos.X).IsEqualTo(100);
            Check.That(pos.Y).IsEqualTo(200);
        }
    }

    [Fact]
    public void Process_AfterLongGapTail_RecoverySample_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 200_000));
        gi.Process(Invalid(0, 0, 220_000));

        var (pos, isInGap) = gi.Process(Valid(300, 400, 240_000));
        Check.That(isInGap).IsFalse();
        Check.That(pos.X).IsEqualTo(300);
        Check.That(pos.Y).IsEqualTo(400);
    }

    [Fact]
    public void Process_AfterLongGapTail_ThenValid_ThenInvalid_TailFlagIsCleared()
    {
        // A valid sample clears the broken-sequence flag, so a later short gap is an ordinary hold
        // and a later cold-start-style early return is not misreported.
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 200_000));       // long gap
        gi.Process(Invalid(0, 0, 220_000));       // tail — in gap
        gi.Process(Valid(300, 400, 240_000));     // recovery clears the flag

        var (_, isInGap) = gi.Process(Invalid(0, 0, 260_000));  // 20ms — short gap hold
        Check.That(isInGap).IsTrue();

        // Second long gap breaks the sequence again — triggering sample and tail alike stay in-gap.
        Check.That(gi.Process(Invalid(0, 0, 400_000)).IsInGap).IsTrue();
        Check.That(gi.Process(Invalid(0, 0, 420_000)).IsInGap).IsTrue();
    }

    [Fact]
    public void Reset_ClearsLongGapTailFlag()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 200_000));  // long gap — tail would now report true

        gi.Reset();

        Check.That(gi.Process(Invalid(0, 0, 220_000)).IsInGap).IsFalse();
    }

    [Fact]
    public void CopyStateFrom_PreservesLongGapTail()
    {
        // A rebuild in the middle of a long gap must not restart the tail as "never seen data".
        var old = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        old.Process(Valid(100, 200, 0));
        old.Process(Invalid(0, 0, 200_000));

        var replacement = new GapInterpolator(new PassThroughFilter(), maxGapMs: 300);
        replacement.CopyStateFrom(old);

        Check.That(replacement.Process(Invalid(0, 0, 220_000)).IsInGap).IsTrue();
    }

    [Fact]
    public void CopyStateFrom_PreservesLastValidPosition()
    {
        var old = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        old.Process(Valid(333, 444, 1_000_000));

        var replacement = new GapInterpolator(new PassThroughFilter(), maxGapMs: 300);
        replacement.CopyStateFrom(old);

        var (pos, isInGap) = replacement.Process(Invalid(0, 0, 1_050_000));
        Check.That(pos.X).IsEqualTo(333);
        Check.That(pos.Y).IsEqualTo(444);
        Check.That(isInGap).IsTrue();
    }

    [Fact]
    public void CopyStateFrom_PreservesInGapBehavior()
    {
        var old = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        old.Process(Valid(100, 200, 0));

        var replacement = new GapInterpolator(new PassThroughFilter(), maxGapMs: 300);
        replacement.CopyStateFrom(old);

        var (pos, isInGap) = replacement.Process(Invalid(0, 0, 50_000));
        Check.That(pos.X).IsEqualTo(100);
        Check.That(pos.Y).IsEqualTo(200);
        Check.That(isInGap).IsTrue();
    }

    [Fact]
    public void CopyStateFrom_PreservesLongGapReset()
    {
        // The carried-over timestamp still makes this a LONG gap (not a short hold) on the replacement —
        // that is what this test pins, now via the filter reset, since the flag alone no longer tells the
        // two apart. It asserted IsInGapFalse before; see Process_LongInvalidGap_IsInGapTrue for why a
        // held position always reports in-gap.
        var filter = new ResetCountingFilter();
        var old = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        old.Process(Valid(100, 200, 0));

        var replacement = new GapInterpolator(filter, maxGapMs: 150);
        replacement.CopyStateFrom(old);

        var (pos, isInGap) = replacement.Process(Invalid(0, 0, 200_000));
        Check.That(pos.X).IsEqualTo(100);
        Check.That(pos.Y).IsEqualTo(200);
        Check.That(isInGap).IsTrue();
        Check.That(filter.ResetCount).IsEqualTo(1);   // long gap broke the sequence...
    }

    [Fact]
    public void CopyStateFrom_ShortGap_DoesNotResetFilter()
    {
        // ...whereas a short hold leaves the filter alone (the contrast for the test above).
        var filter = new ResetCountingFilter();
        var old = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        old.Process(Valid(100, 200, 0));

        var replacement = new GapInterpolator(filter, maxGapMs: 150);
        replacement.CopyStateFrom(old);

        var (_, isInGap) = replacement.Process(Invalid(0, 0, 50_000));
        Check.That(isInGap).IsTrue();
        Check.That(filter.ResetCount).IsEqualTo(0);
    }
}
