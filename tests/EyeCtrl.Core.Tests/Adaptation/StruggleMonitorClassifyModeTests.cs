namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

/// <summary>
/// The multi-feeder fold contract (design §13.2/§13.3), tested directly against
/// <see cref="StruggleMonitor.ClassifyMode"/>. After the `SignalNoise` split every mode in the live map is
/// single-feeder, so this is the only place the two-feeder path is exercised — see §13.3 for why it is kept
/// rather than deleted.
/// </summary>
public class StruggleMonitorClassifyModeTests
{
    private const double High = 0.8;

    private static SignalScore Sig(double score, bool gated) => new(score, gated ? 1.0 : 0.5, gated);

    // Every combination of (gate state × zero/non-zero score) for both feeders where at least one feeder
    // shows struggle. Expected values follow the §13.2 rule: the mode is GATED iff some feeder is gated, and
    // its actionable SCORE is the peak among GATED feeders only — falling back to the raw peak, with
    // GatePassed=false, when no feeder is gated (so monitoring can surface "warming up" without acting).
    [Theory]
    // firstScore, firstGated, secondScore, secondGated, expectedScore, expectedGatePassed
    [InlineData(0.0, true, High, true, High, true)]
    [InlineData(0.0, true, High, false, 0.0, true)]   // F4: gated zero must not be lifted by ungated high
    [InlineData(High, true, 0.0, true, High, true)]
    [InlineData(High, true, High, true, High, true)]
    [InlineData(High, true, 0.0, false, High, true)]
    [InlineData(High, true, High, false, High, true)]
    [InlineData(0.0, false, High, true, High, true)]
    [InlineData(High, false, 0.0, true, 0.0, true)]   // F4 mirror: the direction the spike never reached
    [InlineData(High, false, High, true, High, true)]
    [InlineData(0.0, false, High, false, High, false)] // nothing gated → raw peak, not actionable
    [InlineData(High, false, 0.0, false, High, false)]
    [InlineData(High, false, High, false, High, false)]
    public void ClassifyMode_ScoresFromGatedFeedersOnly(
        double firstScore, bool firstGated, double secondScore, bool secondGated,
        double expectedScore, bool expectedGatePassed)
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, Sig(firstScore, firstGated), Sig(secondScore, secondGated));

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.Mode).IsEqualTo(FailureMode.SustainFailure);
        Check.That(m.Value.Score).IsEqualTo(expectedScore);
        Check.That(m.Value.GatePassed).IsEqualTo(expectedGatePassed);
    }

    // The omission rule: a mode with no positive raw score is dropped entirely, even when a zero-score
    // feeder has passed its volume gate. An omitted mode makes ForMode return null, which the controller's
    // GatedScore reads as 0 — exactly what a present-but-zero mode contributes, so omission is behaviourally
    // equivalent and the split's tolerance identity holds through it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ClassifyMode_OmitsModeWhenNoFeederShowsStruggle(bool firstGated, bool secondGated)
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, Sig(0.0, firstGated), Sig(0.0, secondGated));

        Check.That(m.HasValue).IsFalse();
    }

    /// <summary>
    /// F4 regression, PR #71 review blocker #1 — kept from the original
    /// `Assess_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore_IntoGatedMode`, retargeted from `Assess` to
    /// the fold itself so it survives the `SignalNoise` split (design §13.3). A gated-but-zero-score feeder
    /// combined with an ungated-but-high-score feeder must NOT yield a mode carrying the high score:
    /// pre-fix the peak across ALL feeders was 0.8 with an OR'd gate, which would have driven adaptation on
    /// ungated evidence.
    /// </summary>
    [Fact]
    public void ClassifyMode_GatedZeroScoreFeeder_DoesNotLiftUngatedHighScore()
    {
        SignalScore gatedZero = Sig(0.0, gated: true);      // e.g. zero jitter meeting its volume gate
        SignalScore ungatedHigh = Sig(0.8, gated: false);   // e.g. abandonment below its 4-attempt gate

        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.SustainFailure, gatedZero, ungatedHigh);

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.GatePassed).IsTrue();  // a feeder gated…
        Check.That(m.Value.Score).IsEqualTo(0.0);  // …but the actionable score is the GATED peak, never 0.8
    }

    // A single-feeder mode passes SignalScore.None as the absent second feeder, which is gate-failed with
    // score 0 and so must fold as a no-op. PoorTracking is used simply because it is already single-feeder;
    // the mode identity is irrelevant to what this asserts.
    [Fact]
    public void ClassifyMode_SingleFeeder_IgnoresTheAbsentSecond()
    {
        FailureModeAssessment? m = StruggleMonitor.ClassifyMode(
            FailureMode.PoorTracking, Sig(0.6, gated: true), SignalScore.None);

        Check.That(m.HasValue).IsTrue();
        Check.That(m!.Value.Score).IsEqualTo(0.6);
        Check.That(m.Value.GatePassed).IsTrue();
    }
}
