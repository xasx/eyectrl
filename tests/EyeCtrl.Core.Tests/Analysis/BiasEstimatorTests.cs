namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The bias probe's estimator and rule, validated against streams whose answer is known by
/// construction — the same discipline ticket 2's gates follow. Two classes of stream matter:
/// <list type="bullet">
/// <item>the ACCEPTANCE stream (b = 300 px, σ 9, a larger 250 px glance every 400 ms), which pins
/// recovery and the two forbidden shortcuts (nearest-to-target, band gate);</item>
/// <item>the reviewers' adversarial streams A–F, which pin that a position-dependent or partial
/// reading never comes back as a confident "No trigger".</item>
/// </list>
/// All constants are the debate's or the reviewers': no knob was tuned to make a test pass.
/// </summary>
public class BiasEstimatorTests
{
    private static readonly PhysicalPoint Screen = BiasStreams.Screen;

    /// <summary>The true bias the acceptance stream holds at every target.</summary>
    private static readonly PhysicalPoint TrueBias = new(300, 0);

    // ── Acceptance ────────────────────────────────────────────────────────────

    [Fact]
    public void Acceptance_RecoversB300_DespiteALargerAndNearerDistractor()
    {
        var estimate = BiasEstimator.Measure(BiasStreams.Acceptance(
            seed: 20260926, perTargetDegrees: [45, 90, 135, 225, 270]));

        Check.That(estimate.Bias).IsNotNull();
        Check.That(estimate.Bias!.Value.X).IsCloseTo(TrueBias.X, 20);
        Check.That(estimate.Bias!.Value.Y).IsCloseTo(TrueBias.Y, 20);
        Check.That(estimate.TargetsUsed).IsEqualTo(5);
        Check.That(estimate.Ambiguous).IsFalse();

        // Every chosen probe logs the cluster size and the distance — the debate requires both,
        // because they are what says whether the estimate rests on real holds or on scraps.
        foreach (var probe in estimate.Probes)
        {
            Check.That(probe.Chosen).IsNotNull();
            Check.That(probe.Chosen!.SampleCount).IsGreaterOrEqualThan(BiasEstimator.MinClusterSamples);
            Check.That(probe.DistancePx!.Value).IsCloseTo(300, 20);
        }

        var report = estimate.Describe();
        Check.That(report).Contains("cluster 54 samples");    // the holds, not the 7-sample glances
        Check.That(report).Contains("distance 300.");         // logged per probe
        Check.That(report).Contains("dispersion");            // and the cluster's tightness

        Check.That(BiasDecisionRule.Evaluate(estimate, Screen).Verdict)
            .IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    [Fact]
    public void Acceptance_TheStreamReallyDoesCarryALargerDistractor()
    {
        // The guard on the test above: if a future edit shrank the glances, "largest cluster" would
        // pass for the wrong reason and the acceptance test would stop testing anything.
        var estimate = BiasEstimator.Measure(BiasStreams.Acceptance(
            seed: 20260926, perTargetDegrees: [45, 90, 135, 225, 270]));

        bool someWindowHasABiggerClusterThanItsChoice = estimate.Probes.Any(p =>
            p.Chosen is not null && p.Clusters.Any(c => c.SampleCount > p.Chosen.SampleCount));

        Check.That(someWindowHasABiggerClusterThanItsChoice).IsTrue();
    }

    [Fact]
    public void Acceptance_RandomGlanceDirections_RecoverWithin20For95Percent_AndNeverNoTrigger()
    {
        // The reviewers' P5: the ticket's stream with the glance direction drawn at random per target,
        // 200 seeds. At the old D150 dispersion 5 seeds exceeded 20 px and 13 lost a target, because a
        // glance that landed near the hold merged into it. The tighter dispersion, the split and the
        // place merge are what this pins; and the verdict rule is what makes a bad seed safe: an
        // imprecise seed may come back Recalibrate (the correct action for a 300 px bias) or
        // Inconclusive, but never "No trigger" — and at least 95 % must recover within 20 px.
        int recovered = 0, noTrigger = 0, imprecise = 0;
        for (uint seed = 1; seed <= 200; seed++)
        {
            var estimate = BiasEstimator.Measure(BiasStreams.Acceptance(seed));
            var decision = BiasDecisionRule.Evaluate(estimate, Screen);

            if (decision.Verdict == BiasDecisionRule.BiasVerdict.NoTrigger) noTrigger++;
            double? error = estimate.Bias is { } bias
                ? Math.Sqrt(Math.Pow(bias.X - TrueBias.X, 2) + Math.Pow(bias.Y - TrueBias.Y, 2))
                : null;
            if (error is { } e && e <= 20) recovered++;
            else imprecise++;
        }

        Check.That(recovered).IsGreaterOrEqualThan(190);   // ≥ 95 % of seeds
        Check.That(noTrigger).IsEqualTo(0);                // a large bias is never "No trigger"
        Check.That(imprecise).IsLessOrEqualThan(10);       // the bar, named
    }

    [Fact]
    public void ClustersStartingInsideTheOnsetGuard_AreIgnored()
    {
        // The first 300 ms after a target appears are the saccade and the search, not a hold. Samples
        // there must not become clusters even when they are perfectly steady.
        var target = new PhysicalPoint(500, 500);
        var samples = new List<GroundTruthSample>();
        for (int i = 0; i < 10; i++)      // 0–270 ms, all before the guard
            samples.Add(new GroundTruthSample(target.X - 100, target.Y, i * 30_000, Valid: true));
        for (int i = 10; i < 30; i++)     // 300 ms onward: eligible, held 100 px to the right
            samples.Add(new GroundTruthSample(target.X + 100, target.Y, i * 30_000, Valid: true));

        var window = new GroundTruthWindow(0, target.X, target.Y, 0, 0, 29 * 30_000, samples.Count)
        {
            Samples = samples,
        };

        var clusters = BiasEstimator.ExtractClusters(window);

        Check.That(clusters).HasSize(1);
        Check.That(clusters[0].Centroid.X).IsCloseTo(target.X + 100, 1e-9);
        Check.That(clusters[0].StartUs).IsGreaterOrEqualThan(300_000L);
    }

    [Fact]
    public void AContaminatedRun_IsSplitIntoTheTwoPlacesItVisited()
    {
        // A hold at +300 followed immediately by a glance at +250: the dispersion box swallows both
        // (50 px < D75) into one run, and its centroid would land at ~275 — tens of pixels off the
        // truth. The bimodal split is what keeps that run from becoming a fabricated third place.
        var target = new PhysicalPoint(500, 500);
        var samples = new List<GroundTruthSample>();
        long t = 300_000;
        for (int i = 0; i < 8; i++, t += 30_000)
            samples.Add(new GroundTruthSample(target.X + 300, target.Y, t, Valid: true));
        for (int i = 0; i < 8; i++, t += 30_000)
            samples.Add(new GroundTruthSample(target.X + 250, target.Y, t, Valid: true));

        var window = new GroundTruthWindow(0, target.X, target.Y, 0, 0, t - 30_000, samples.Count)
        {
            Samples = samples,
        };

        var clusters = BiasEstimator.ExtractClusters(window);

        Check.That(clusters).HasSize(2);
        Check.That(clusters[0].Centroid.X).IsCloseTo(target.X + 300, 1e-9);
        Check.That(clusters[0].SampleCount).IsEqualTo(8);
        Check.That(clusters[1].Centroid.X).IsCloseTo(target.X + 250, 1e-9);
        Check.That(clusters[1].SampleCount).IsEqualTo(8);
    }

    [Fact]
    public void RepeatedVisitsToTheSamePlace_AreOneClusterWithAllTheEvidence()
    {
        // A stream that returns to its hold five times produces five runs there. Ranking them as five
        // candidates would bury a smaller but distinct look-away under the cap and split one place's
        // evidence into five weak votes; they are merged by place first.
        var target = new PhysicalPoint(500, 500);
        var samples = new List<GroundTruthSample>();
        long t = 300_000;
        for (int visit = 0; visit < 5; visit++)
        {
            // A pixel of visit-to-visit jitter so "the same place" is a real distance test, not exact
            // duplicate centroids that any merge threshold would accept.
            double jitter = (visit % 2 == 0) ? 1.0 : -1.0;
            for (int i = 0; i < 6; i++, t += 30_000)
                samples.Add(new GroundTruthSample(target.X + 100 + jitter, target.Y + jitter, t, Valid: true));
            for (int i = 0; i < 3; i++, t += 30_000)   // away, below MinClusterSamples and far outside the box
                samples.Add(new GroundTruthSample(target.X + 900, target.Y + 700, t, Valid: true));
        }

        var window = new GroundTruthWindow(0, target.X, target.Y, 0, 0, t - 30_000, samples.Count)
        {
            Samples = samples,
        };

        var clusters = BiasEstimator.ExtractClusters(window);

        Check.That(clusters).HasSize(1);
        Check.That(clusters[0].SampleCount).IsEqualTo(30);
        Check.That(clusters[0].Centroid.X).IsCloseTo(target.X + 100, 2);
    }

    [Fact]
    public void GlanceLandingNearTheHold_DoesNotMergeIntoAWrongConfidentVerdict()
    {
        // Reviewer A's P6: at every target the user's glance lands 50 px short of the hold and 60 px
        // off-axis. At the old D150 those two segments merged and the estimate came out 42 px off with
        // a confident verdict. At D75 they separate; the glance is then a perfectly consistent field
        // of its own, so the honest answer is Ambiguous (Inconclusive), not a silent pick.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(9);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            long from = t;
            var samples = new List<GroundTruthSample>();
            for (int c = 0; c < 10; c++)
            {
                for (int i = 0; i < 6; i++, t += BiasStreams.StepUs)
                    samples.Add(new GroundTruthSample(target.X + 300 + (noise.NextGaussian() * 9), target.Y + (noise.NextGaussian() * 9), t, true));
                for (int i = 0; i < 7; i++, t += BiasStreams.StepUs)
                    samples.Add(new GroundTruthSample(target.X + 250 + (noise.NextGaussian() * 9), target.Y + 60 + (noise.NextGaussian() * 9), t, true));
            }
            windows.Add(new GroundTruthWindow(id, target.X, target.Y, from, from, t - BiasStreams.StepUs, samples.Count) { Samples = samples });
        }

        var estimate = BiasEstimator.Measure(BiasStreams.Session(windows));
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
        bool honestlyRecovered = estimate.Bias is { } b
            && Math.Abs(b.X - 300) <= 20 && Math.Abs(b.Y) <= 20;
        Check.That(estimate.Ambiguous || honestlyRecovered).IsTrue();
    }

    // ── The reviewers' named adversarial streams, end to end ──────────────────

    [Fact]
    public void StreamA_PositionDependentBias_Recalibrates()
    {
        // (65, 62, 10, 10, 10): these are the decision-rule unit test's own numbers, which the old
        // single-consensus estimator turned into a confident "No trigger" by dropping targets 0 and 1.
        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.A()), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
        Check.That(decision.MeasuredTargets).HasSize(5);
    }

    [Fact]
    public void StreamB_LeftMinus40RightPlus100_Recalibrates()
    {
        // A translation plus an x-scale: the right column is 100 px, which the old rule never saw.
        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.B()), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
        Check.That(decision.MeasuredTargets.Count(t => t.MagnitudePx >= 60)).IsGreaterOrEqualThan(2);
    }

    [Fact]
    public void StreamC_RadialScaleError_Recalibrates()
    {
        // The textbook corner-degradation shape. The old code said "No target produced a usable
        // cluster" while its own report said 5 of 5 targets had clusters.
        var estimate = BiasEstimator.Measure(BiasStreams.C());
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.TargetsWithData).IsEqualTo(5);
        Check.That(estimate.TargetsUsed).IsEqualTo(5);
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    [Fact]
    public void StreamD_ConsistentDistractor_IsAmbiguousAndInconclusive()
    {
        // Truth (20, 0) with a slightly longer consistent rest at (20, −160) at every target. Both
        // solutions fit a field; the old code silently reported 159 px. The honest answer is
        // Ambiguous → Inconclusive.
        var estimate = BiasEstimator.Measure(BiasStreams.D());
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.Ambiguous).IsTrue();
        Check.That(estimate.Rival).IsNotNull();
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("two consistent solutions");
    }

    [Fact]
    public void StreamF_TwoOfFiveMeasured_IsInconclusive_NotNoTrigger()
    {
        var estimate = BiasEstimator.Measure(BiasStreams.F());
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.TargetsWithData).IsEqualTo(2);
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("only 2 of 5");
    }

    [Fact]
    public void SparseHolds_FailSafe()
    {
        // Reviewer B's stream E: holds of 4 samples are below MinClusterSamples. No estimate, and the
        // verdict may not claim the offset is small.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(5);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            windows.Add(BiasStreams.Window(i, targets[i], new PhysicalPoint(300, 0), ref t, noise,
                holdN: 4, distractorAbs: new PhysicalPoint(targets[i].X - 500, targets[i].Y + 400), dN: 3));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    // ── Round 2: the verdict must be decidable, bounded and honest ────────────

    [Fact]
    public void StreamG1_CentreUnmeasured_IsInconclusive_NotNoTrigger()
    {
        // G1: the centre window has no steady hold; the true centre is 90 px (the centre arm fires)
        // and the corners are 30 px. A "No trigger" here would never have tested the centre arm.
        var decision = BiasDecisionRule.Evaluate(
            BiasEstimator.Measure(UniformBias([30, 30, 90, 30, 30], [10, 10, 0, 10, 10])), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("centre");
    }

    [Fact]
    public void StreamG2_OneUnmeasuredWithOneMeasuredAtSixty_IsInconclusive_NotNoTrigger()
    {
        // G2: TL and BR are truly 70 px and the others 10. TL has no hold, and BR is measured at
        // ~69 px — one measured target is already over the bar and the unmeasured one could be the
        // second, so the 2-of-5 arm is not decidable.
        var decision = BiasDecisionRule.Evaluate(
            BiasEstimator.Measure(UniformBias([70, 10, 10, 70, 10], [0, 10, 10, 10, 10])), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("not decidable");
    }

    [Fact]
    public void Boundary_ThreeOfFiveMeasured_CannotSayNoTrigger()
    {
        // Three windows only (an aborted probe), all clustered, the centre measured, every measured
        // bias small. Decidability alone would allow "No trigger" — but two unmeasured targets could
        // complete the 2-of-5 arm, and the count bar (4) is what refuses it.
        var targets = BiasStreams.Layout;
        int centre = BiasStreams.CentreIndex();
        var noise = new BiasStreams.Noise(41);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        int[] ids = [0, centre, 3];
        for (int n = 0; n < ids.Length; n++)
            windows.Add(BiasStreams.Window(ids[n], targets[ids[n]], new PhysicalPoint(10, 0), ref t, noise));

        var estimate = BiasEstimator.Measure(BiasStreams.Session(windows));
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.TargetsWithData).IsEqualTo(3);
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
    }

    [Theory]
    [InlineData(10.0, 10)]   // the secondary 60 px from the hold, longer than it
    [InlineData(0.0, 10)]    // 70 px away
    [InlineData(-10.0, 10)]  // 80 px away
    [InlineData(10.0, 6)]    // 60 px away but SHORTER than the hold
    public void StreamG6_ASecondFixationAtACorner_IsNeverNoTrigger(double secondaryOffset, int secondarySamples)
    {
        // A real affine field — top row 70 px, bottom row 10, centre 40, so two targets are over the
        // bar and the truth recalibrates — plus a second fixation at the weakly constrained TL corner.
        // The old code let the field absorb it (TL read 3–9 px) and answered "No trigger".
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(26);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            double yn = (target.Y - (Screen.Y / 2)) / 1080.0;
            var bias = new PhysicalPoint(40 - (30 * yn), 0);
            windows.Add(i == 0
                ? BiasStreams.Window(i, target, bias, ref t, noise, holdN: 8,
                    distractorAbs: new PhysicalPoint(target.X + secondaryOffset, target.Y), dN: secondarySamples)
                : BiasStreams.Window(i, target, bias, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Theory]
    [InlineData(12)]   // 1.5x the hold
    [InlineData(20)]   // 2.5x the hold
    public void StreamG9_AFixedScreenRest_IsNeverRecalibrate(int readSamples)
    {
        // The truth is 10 px everywhere, but at every target the user also reads the instruction text
        // at a FIXED screen spot for longer than they hold the dot. That rest is v = S − P: an exact
        // affine field with slope −1, so only the gradient bound rejects it (the old estimator
        // reported Recalibrate with 1110 px distances at 2.5x).
        var decision = BiasDecisionRule.Evaluate(
            BiasEstimator.Measure(BiasStreams.FixedSpotRest(
                29, new PhysicalPoint(10, 0), new PhysicalPoint(1920, 90), holdN: 8, readN: readSamples)),
            Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    [Fact]
    public void StreamG4_TranslationPlusScale_StillRecalibrates()
    {
        // The gradient bound must not reject real residuals: a 30 px translation plus a 3 % scale
        // (gradient 0.03) is still a field, and the corners are over the bar.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(24);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var target = targets[i];
            var bias = new PhysicalPoint(
                30 + (0.03 * (target.X - (Screen.X / 2))), 0.03 * (target.Y - (Screen.Y / 2)));
            windows.Add(BiasStreams.Window(i, target, bias, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    [Theory]
    [InlineData(20)]   // the 2-of-5 arm's own threshold: a false Recalibrate here costs a Tobii trip
    [InlineData(45)]   // the boundary between the bars
    public void Sweep_SmallBias_NeverRecalibrates_AndSaysNoTriggerAtNinetyFivePercent(int biasX)
    {
        // Reviewer B's small-bias sweep: the ticket's own glance shape (250 px glance, one sample
        // longer than the hold) at a bias the rule should call clean. Before the gradient bound about
        // one seed in five gave a confident Recalibrate.
        var result = BiasStreams.Sweep(
            seed => BiasStreams.GlanceStream(seed, new PhysicalPoint(biasX, 0), cycles: 7),
            new PhysicalPoint(biasX, 0));

        Check.That(result.Recalibrate).IsEqualTo(0);
        Check.That(result.NoTrigger).IsGreaterOrEqualThan(190);
        Check.That(result.Within20).IsGreaterOrEqualThan(190);
    }

    [Fact]
    public void Sweep_IncidentalSecondPlaces_AreNotCalledAmbiguous()
    {
        // The false-positive side of ambiguity: a real single bias (20, 0) with an incidental second
        // place at a random spot 150–400 px away at every target, about 60 % of the hold's size. The
        // answer is No trigger, and calling these Ambiguous would make the probe useless.
        int ambiguous = 0, noTrigger = 0;
        for (uint seed = 1; seed <= 100; seed++)
        {
            var estimate = BiasEstimator.Measure(BiasStreams.GlanceStream(
                seed, new PhysicalPoint(20, 0), cycles: 7, holdN: 8, glanceN: 5, glanceRMin: 150, glanceRMax: 400));
            var decision = BiasDecisionRule.Evaluate(estimate, Screen);
            if (estimate.Ambiguous) ambiguous++;
            if (decision.Verdict == BiasDecisionRule.BiasVerdict.NoTrigger) noTrigger++;
        }

        Check.That(ambiguous).IsLessOrEqualThan(5);   // measured 3/100; the cost is Inconclusive, not a wrong verdict
        Check.That(noTrigger).IsGreaterOrEqualThan(95);
    }

    /// <summary>A five-window session with the given per-target x biases and per-target cycle counts.</summary>
    private static GroundTruthSession UniformBias(double[] biasesX, int[] cycles)
    {
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(21);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(BiasStreams.Window(i, targets[i], new PhysicalPoint(biasesX[i], 0), ref t, noise, cycles: cycles[i]));
        return BiasStreams.Session(windows);
    }

    // ── Decision rule unit tests (hand-built estimates) ───────────────────────

    [Fact]
    public void Decision_TriggersWhenTwoTargetsAreAtSixtyPixels()
    {
        var estimate = BuildEstimate(
            new PhysicalPoint(65, 0), new PhysicalPoint(62, 0),
            new PhysicalPoint(10, 0), new PhysicalPoint(10, 0), new PhysicalPoint(10, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
        Check.That(decision.Reason).Contains("Tobii Dynavox Eye Tracking Settings");
    }

    [Fact]
    public void Decision_TriggersOnTheCentreAloneAtSeventyFivePixels()
    {
        var estimate = BuildEstimate(
            new PhysicalPoint(5, 0), new PhysicalPoint(5, 0),
            new PhysicalPoint(80, 0), new PhysicalPoint(5, 0), new PhysicalPoint(5, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
        Check.That(decision.CentreMagnitudePx!.Value).IsEqualTo(80.0);
    }

    [Fact]
    public void Decision_NoTrigger_OnlyWithFullCoverageAndFourMeasured()
    {
        // Index 2 is the centre for the five-point layout. 70 px is inside R75 and only one target is
        // at/above 60, so neither arm fires; with every target measured the answer is No trigger.
        var estimate = BuildEstimate(
            new PhysicalPoint(5, 0), new PhysicalPoint(5, 0),
            new PhysicalPoint(70, 0), new PhysicalPoint(5, 0), new PhysicalPoint(5, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
        Check.That(decision.Reason).Contains("No trigger");
    }

    [Fact]
    public void Decision_RefusesNoTrigger_WhenAClusteredTargetWasDropped()
    {
        // Four targets at 10 px, one with clusters that disagree and were dropped: the surviving
        // numbers are small, but "small" cannot be claimed while a clustered target is unexplained.
        var estimate = BuildEstimateWithCoverage(
            [new PhysicalPoint(10, 0), new PhysicalPoint(10, 0), new PhysicalPoint(10, 0),
             new PhysicalPoint(10, 0), null],
            dropped: [4]);

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("disagree");
    }

    [Fact]
    public void Decision_RefusesNoTrigger_WithOnlyTwoTargetsMeasured()
    {
        var estimate = BuildEstimateWithCoverage(
            [new PhysicalPoint(30, 0), new PhysicalPoint(30, 0), null, null, null], dropped: []);

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
    }

    [Fact]
    public void Decision_Ambiguous_IsInconclusive()
    {
        var estimate = BuildEstimate(new PhysicalPoint(20, 0), new PhysicalPoint(20, 0),
            new PhysicalPoint(20, 0), new PhysicalPoint(20, 0), new PhysicalPoint(20, 0)) with
        {
            Ambiguous = true,
            Rival = new BiasEstimator.Alternative(new PhysicalPoint(20, -160), 5, 90),
        };

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("two consistent solutions");
    }

    /// <summary>Builds an estimate whose five probes were all measured at the given magnitudes.</summary>
    private static BiasEstimator.Estimate BuildEstimate(params PhysicalPoint[] perTargetBias) =>
        BuildEstimateWithCoverage(perTargetBias.Select(b => (PhysicalPoint?)b).ToArray(), dropped: []);

    private static BiasEstimator.Estimate BuildEstimateWithCoverage(PhysicalPoint?[] perTargetBias, int[] dropped)
    {
        var targets = CalibrationTargetPositionHelper.GetPositions(5, Screen);
        var probes = new List<BiasEstimator.Probe>(perTargetBias.Length);
        double sumX = 0, sumY = 0;
        int used = 0;
        for (int i = 0; i < perTargetBias.Length; i++)
        {
            var target = targets[i];
            if (perTargetBias[i] is not { } bias)
            {
                // No chosen cluster; the target either has no clusters or had them dropped.
                if (dropped.Contains(i))
                {
                    var off = new BiasEstimator.Cluster(new PhysicalPoint(target.X + 300, target.Y), 40, 0, 1_000_000, 40);
                    probes.Add(new BiasEstimator.Probe(i, target, [off], Chosen: null));
                }
                else
                {
                    probes.Add(new BiasEstimator.Probe(i, target, [], Chosen: null));
                }
                continue;
            }

            var centroid = new PhysicalPoint(target.X + bias.X, target.Y + bias.Y);
            var cluster = new BiasEstimator.Cluster(centroid, 50, 0, 1_000_000, 30);
            probes.Add(new BiasEstimator.Probe(i, target, [cluster], cluster));
            sumX += bias.X;
            sumY += bias.Y;
            used++;
        }

        var mean = used == 0 ? (PhysicalPoint?)null : new PhysicalPoint(sumX / used, sumY / used);
        int withData = probes.Count(p => p.Clusters.Count > 0);
        return new BiasEstimator.Estimate(mean, withData, used, probes,
            Ambiguous: false, Rival: null, MaxFieldResidualPx: 0);
    }
}
