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
        // Never a silent wrong magnitude: either the magnitude is right, or it is flagged — Ambiguous
        // (no decision) or, when every reading still recalibrates, MagnitudeUncertain (follow-up 7).
        Check.That(estimate.Ambiguous || estimate.MagnitudeUncertain || honestlyRecovered).IsTrue();
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
    public void UnanimousRecalibrate_WithAMagnitudeOnlyRival_Recalibrates_AndSaysTheSizeIsUncertain()
    {
        // Follow-up 7, the shape of the user's real probe 2 (2026-09-26): two comparably supported
        // readings that BOTH prove the per-target arm — a hold at (100, 0) and a consistent rest at
        // (100, −90) at every target, every magnitude ≥ 100 px — but whose means sit 90 px apart.
        // The mean arm used to make this Inconclusive although no reading could avoid Recalibrate.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(71);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            windows.Add(BiasStreams.Window(i, targets[i], new PhysicalPoint(100, 0), ref t, noise, holdN: 8,
                distractorAbs: new PhysicalPoint(targets[i].X + 100, targets[i].Y - 90), dN: 7));
        }

        var estimate = BiasEstimator.Measure(BiasStreams.Session(windows));
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.Ambiguous).IsFalse();
        Check.That(estimate.MagnitudeUncertain).IsTrue();
        Check.That(estimate.Rival).IsNotNull();
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
        Check.That(decision.Reason).Contains("The size is uncertain");
        // The trigger is per target, never per mean — say so, and name both arms (follow-up 8, FU7-1).
        Check.That(decision.Reason).Contains("both readings still meet a Recalibrate arm");
        Check.That(decision.Reason).Contains("≥ 2 targets at or above 60 px, or the centre at or above 75 px");
        // The operator report carries the same warning, not only the one-line reason (follow-up 8, M-d).
        Check.That(estimate.Describe()).Contains("MAGNITUDE UNCERTAIN");
        Check.That(estimate.Describe()).Not.Contains("AMBIGUOUS");
    }

    [Fact]
    public void AVerdictRival_OutranksAMagnitudeOnlyRival()
    {
        // Stream D plus a second consistent rest: truth (20, 0) ×8, rest (20, −160) ×9 — the winner,
        // Recalibrate — and rest (20, −260) ×6, which also recalibrates but sits 100 px from the
        // winner's mean. The truth rival flips the verdict, so the answer must stay Inconclusive; if the
        // magnitude-only rival were allowed to speak for the rival set, a No-trigger truth would be
        // silenced into a confident Recalibrate (follow-up 7's M3).
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(73);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int id = 0; id < 5; id++)
        {
            var target = targets[id];
            long from = t;
            var samples = new List<GroundTruthSample>();
            for (int i = 0; i < 10; i++, t += BiasStreams.StepUs)   // onset scatter inside the guard
                samples.Add(new GroundTruthSample(target.X - 400 + (i * 40), target.Y - 300 + (i * 30), t, true));
            for (int c = 0; c < 10; c++)
            {
                foreach (var (dx, dy, n) in new[] { (20.0, 0.0, 8), (20.0, -160.0, 9), (20.0, -260.0, 6) })
                {
                    for (int i = 0; i < n; i++, t += BiasStreams.StepUs)
                        samples.Add(new GroundTruthSample(target.X + dx + (noise.NextGaussian() * 9), target.Y + dy + (noise.NextGaussian() * 9), t, true));
                }
            }
            windows.Add(new GroundTruthWindow(id, target.X, target.Y, from, null, t - BiasStreams.StepUs, samples.Count) { Samples = samples });
        }

        var estimate = BiasEstimator.Measure(BiasStreams.Session(windows));
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.MagnitudeUncertain).IsFalse();
        Check.That(estimate.Ambiguous).IsTrue();
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
    }

    [Fact]
    public void UnanimousNoTrigger_WithAMagnitudeOnlyRival_StaysInconclusive()
    {
        // The other half of follow-up 7, deliberately NOT relaxed: only a Recalibrate may pass a
        // magnitude-only rival, so the change can never mint a "No trigger". A hold at (10, 0) and a
        // consistent rest at (10, −45): both readings are clearly small, the means 45 px apart.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(72);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            windows.Add(BiasStreams.Window(i, targets[i], new PhysicalPoint(10, 0), ref t, noise, holdN: 8,
                distractorAbs: new PhysicalPoint(targets[i].X + 10, targets[i].Y - 45), dN: 7));
        }

        var estimate = BiasEstimator.Measure(BiasStreams.Session(windows));
        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(estimate.Ambiguous).IsTrue();
        Check.That(estimate.MagnitudeUncertain).IsFalse();
        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
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
        // Centre + two corners that are not collinear with it (the TL–centre–BR diagonal IS collinear,
        // and a three-point field fit on a line is singular by construction).
        int[] ids = [centre, 3, 4];
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

    [Fact]
    public void RevisitsMoreThanTheMergeRadiusFromTheAnchor_StaySeparate()
    {
        // Three runs 25 px apart. With an ANCHORED place, the 25 px run merges into the first and the
        // 35 px run does not; with a running centroid the middle run drags the place to 12.5 px and the
        // third lands within 28 px of THAT, so all three chain into one blended place — reviewer A's
        // chaining mechanism. Spaced for the 28 px linkage: at 0/25/50 the two implementations agreed
        // and the pin lost its teeth.
        var target = new PhysicalPoint(500, 500);
        var samples = new List<GroundTruthSample>();
        long t = 300_000;
        foreach (double offset in new[] { 0.0, 25.0, 35.0 })
        {
            for (int i = 0; i < 6; i++, t += 30_000)
                samples.Add(new GroundTruthSample(target.X + 100 + offset, target.Y, t, Valid: true));
            for (int i = 0; i < 3; i++, t += 30_000)   // away, below MinClusterSamples
                samples.Add(new GroundTruthSample(target.X + 900, target.Y + 700, t, Valid: true));
        }

        var window = new GroundTruthWindow(0, target.X, target.Y, 0, 0, t - 30_000, samples.Count)
        {
            Samples = samples,
        };

        var clusters = BiasEstimator.ExtractClusters(window);

        Check.That(clusters).HasSize(2);
        Check.That(clusters[0].SampleCount).IsEqualTo(12);   // the 0 and 25 px runs
        Check.That(clusters[1].SampleCount).IsEqualTo(6);    // the 35 px run, beyond the anchor's radius
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

    // ── Round 4: the safe slack, jitter, and honest degraded-target reasons ───

    [Theory]
    [InlineData(20.0, 6, 7)]
    [InlineData(20.0, 5, 8)]
    [InlineData(25.0, 6, 7)]
    [InlineData(25.0, 5, 8)]
    public void Sweep_TwoCornerSlack_NeverGivesAWrongNoTrigger(double centreX, int holdSamples, int restSamples)
    {
        // Reviewer B's R4-1: a modestly non-affine truth (top row 70/62, bottom 10, centre C) whose
        // true verdict is Recalibrate, with rests 30 px off at both top corners, slightly longer than
        // the hold. The rests fit better, so the truth is the rival; at a 5 px slack the truth was
        // dropped and the verdict a confident false "No trigger" on 10–69 of 100 seeds. At 10 it is at
        // most 1 in 100.
        int wrongNoTrigger = 0;
        for (uint seed = 1; seed <= 100; seed++)
        {
            var decision = BiasDecisionRule.Evaluate(
                BiasEstimator.Measure(BiasStreams.TwoCornerSlack(seed, centreX, holdSamples, restSamples)),
                Screen);
            if (decision.Verdict == BiasDecisionRule.BiasVerdict.NoTrigger) wrongNoTrigger++;
        }

        Check.That(wrongNoTrigger).IsLessOrEqualThan(1);
    }

    [Fact]
    public void Sweep_Jitter_NoWrongVerdicts_AndNoTriggerAtTheMeasuredAndTiredSigmas()
    {
        // A clean 20 px bias, no distractor, varying fixation jitter. At σ ≥ 12 the 28 px linkage
        // fragments fixations, and judging the fragment rivals WITH the minority guard marked every
        // clean small bias Ambiguous (No trigger fell to 76/26/21 of 100 at σ 12/15/20). Rivals are
        // judged without the guard now; 0 Recalibrate anywhere, No trigger ≥ 95 % at σ 9 and 12.
        var noTriggerBySigma = new Dictionary<double, int>();
        foreach (double sigma in new[] { 9.0, 12.0, 15.0, 20.0 })
        {
            int noTrigger = 0, recalibrate = 0;
            for (uint seed = 1; seed <= 100; seed++)
            {
                var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Jitter(seed, sigma)), Screen);
                if (decision.Verdict == BiasDecisionRule.BiasVerdict.NoTrigger) noTrigger++;
                if (decision.Verdict == BiasDecisionRule.BiasVerdict.Recalibrate) recalibrate++;
            }
            Check.That(recalibrate).IsEqualTo(0);
            noTriggerBySigma[sigma] = noTrigger;
        }

        Check.That(noTriggerBySigma[9.0]).IsGreaterOrEqualThan(95);
        Check.That(noTriggerBySigma[12.0]).IsGreaterOrEqualThan(95);
        // σ 15 and 20 are reported, not gated: the linkage fragments real fixations there and the
        // honest answer is Inconclusive.
        Check.That(noTriggerBySigma[15.0] + noTriggerBySigma[20.0]).IsGreaterOrEqualThan(0);
    }

    [Theory]
    [InlineData(20.0, 8)]
    [InlineData(25.0, 8)]
    [InlineData(25.0, 12)]
    [InlineData(20.0, 16)]
    public void StreamG16_OneOfTwoOverBarTargetsBlended_StaysInconclusive(double gap, int secondSamples)
    {
        // TL and TR are truly 62 px (the arm fires), but a second place blends TR down to 48–52. The
        // per-target rule tolerates the underestimate and answers Inconclusive; a tighter rule that
        // counts only the remaining over-bar target would say "No trigger" (reviewer A's G16).
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(161);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            double yn = (targets[i].Y - (Screen.Y / 2)) / 1080.0;
            var bias = new PhysicalPoint(36 - (26 * yn), 0);
            windows.Add(i == 4
                ? BiasStreams.Window(i, targets[i], bias, ref t, noise, holdN: 8,
                    distractorAbs: new PhysicalPoint(targets[i].X + bias.X - gap, targets[i].Y), dN: secondSamples)
                : BiasStreams.Window(i, targets[i], bias, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
    }

    [Theory]
    [InlineData(55.0)]
    [InlineData(70.0)]
    [InlineData(90.0)]
    public void StreamG15_OneDegradedCorner_OwnsItsReason(double corner)
    {
        // One target far above the per-target bar cannot fire the 2-of-5 arm alone, but a second
        // near-bar target cannot be ruled out. The reason must say that, name the target, and point at
        // the Tobii recalibration — not claim a 90 px reading is "within 10 px of the bars".
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(151);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(BiasStreams.Window(i, targets[i], i == 0 ? new PhysicalPoint(corner, 0) : new PhysicalPoint(10, 0), ref t, noise));

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
        Check.That(decision.Reason).Contains("target 0");
        Check.That(decision.Reason).Contains("cannot fire the 2-of-5 arm");
        Check.That(decision.Reason).Contains("Tobii Dynavox Eye Tracking Settings");
        Check.That(decision.Reason).Not.Contains("within 10 px of the bars");
    }

    [Fact]
    public void StreamG15_OneCornerInsideTheBand_StillSaysNoTrigger()
    {
        // 45 px at one corner is clearly small, so the honest answer is still No trigger.
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(151);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(BiasStreams.Window(i, targets[i], i == 0 ? new PhysicalPoint(45, 0) : new PhysicalPoint(10, 0), ref t, noise));

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Fact]
    public void StreamK_RestFirstOrder_StillRecalibrates()
    {
        // The same two places as stream K, but the rest runs FIRST in every cycle: an implementation
        // that dropped the sample-count tie-break would pick the rest (it is enumerated first and has
        // fewer samples) and answer No trigger. The tie-break must choose the 66-sample truth.
        var decision = BiasDecisionRule.Evaluate(
            BiasEstimator.Measure(BiasStreams.TwoPlaceRestFirst(
                new PhysicalPoint(100, 0), new PhysicalPoint(10, 0), holdSamples: 11, restSamples: 5)),
            Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
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
        // Index 2 is the centre for the five-point layout. Every measured |b| must sit 10 px clear of
        // the 60 px per-target bar (so below 50 — the centre included, which makes the separate
        // centre clause the looser of the two and inert at this δ), and the centre must also be clear
        // of 75 by 10. Corners 5, centre 49: full coverage, clearly small → No trigger.
        var estimate = BuildEstimate(
            new PhysicalPoint(5, 0), new PhysicalPoint(5, 0),
            new PhysicalPoint(49, 0), new PhysicalPoint(5, 0), new PhysicalPoint(5, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
        Check.That(decision.Reason).Contains("No trigger");
    }

    [Fact]
    public void Decision_GuardBand_TurnsNearBarReadingsIntoInconclusive()
    {
        // δ = 10: No trigger needs every |b| < 50 (the per-target bar less δ), and the centre clear of
        // 65 (the centre bar less δ — subsumed by the per-target clause here).
        Check.That(BiasDecisionRule.Evaluate(
            BuildEstimate(new PhysicalPoint(49, 0), new PhysicalPoint(49, 0), new PhysicalPoint(49, 0),
                new PhysicalPoint(49, 0), new PhysicalPoint(49, 0)), Screen).Verdict)
            .IsEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);

        Check.That(BiasDecisionRule.Evaluate(
            BuildEstimate(new PhysicalPoint(51, 0), new PhysicalPoint(51, 0), new PhysicalPoint(51, 0),
                new PhysicalPoint(51, 0), new PhysicalPoint(51, 0)), Screen).Verdict)
            .IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);

        // 55 px at the centre is below its own 75 px bar, but the per-target band is what binds:
        // "No trigger" means clearly small, and 55 is within 10 px of the 60 px per-target bar.
        Check.That(BiasDecisionRule.Evaluate(
            BuildEstimate(new PhysicalPoint(10, 0), new PhysicalPoint(10, 0), new PhysicalPoint(55, 0),
                new PhysicalPoint(10, 0), new PhysicalPoint(10, 0)), Screen).Verdict)
            .IsEqualTo(BiasDecisionRule.BiasVerdict.Inconclusive);
    }

    [Fact]
    public void StreamK_TwoPlaceTruthWinsTheTieBreak_AndRecalibrates()
    {
        // Truth (100, 0) at every target with a consistent near-target rest at (10, 0) holding 5 of
        // 11 cycles (truth-to-rest 2.2). The rest is NOT comparable support (below the 0.5 ratio), so
        // no ambiguity arm can mask the choice: the sample-count tie-break must pick the truth, and
        // flipping it (prefer fewer samples) makes the rest win and this red.
        var decision = BiasDecisionRule.Evaluate(
            BiasEstimator.Measure(BiasStreams.TwoPlace(
                new PhysicalPoint(100, 0), new PhysicalPoint(10, 0), holdSamples: 11, restSamples: 5)),
            Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    // ── Round 3: a minority wander must not complete a "No trigger" ───────────

    [Theory]
    [InlineData(false, 15.0, 6)]
    [InlineData(true, 15.0, 6)]
    [InlineData(true, 30.0, 6)]
    [InlineData(true, 15.0, 5)]
    public void StreamG13a_CentreOnlyBias_WithABriefWander_IsNeverNoTrigger(
        bool wander, double wanderX, int wanderSamples)
    {
        // A centre-only 90 px bias cannot form a bounded field, so every 5-target assignment from the
        // real holds fails. ONE 5–6 sample wander at the centre that happens to fit used to complete a
        // 5-target assignment and answer "No trigger, centre 13–29 px".
        var targets = BiasStreams.Layout;
        int centre = BiasStreams.CentreIndex();
        var noise = new BiasStreams.Noise(131);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
            windows.Add(i == centre
                ? BiasStreams.WithWander(i, targets[i], new PhysicalPoint(90, 0),
                    wander ? new PhysicalPoint(wanderX, 0) : null, wanderSamples, ref t, noise)
                : BiasStreams.WithWander(i, targets[i], new PhysicalPoint(10, 0), null, 0, ref t, noise));

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Theory]
    [InlineData("centre90", 6)]
    [InlineData("centre90", 10)]
    [InlineData("diag110", 6)]
    public void Sweep_G13RandomWander_AddsNoFalseNoTrigger(string kind, int wanderSamples)
    {
        // 200 seeds, one random 180 ms wander 50–120 px from the deviant target's hold. Before the
        // minority-pick guard: 22 / 21 / 7 false "No trigger". Now zero, with the rest Inconclusive.
        int noTrigger = 0;
        for (uint seed = 1; seed <= 200; seed++)
        {
            var decision = BiasDecisionRule
                .Evaluate(BiasEstimator.Measure(BiasStreams.G13(seed, kind, wanderSamples)), Screen);
            if (decision.Verdict == BiasDecisionRule.BiasVerdict.NoTrigger) noTrigger++;
        }

        Check.That(noTrigger).IsEqualTo(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamG13b_DegradedDiagonalCorners_WithWanders_IsNeverNoTrigger(bool wander)
    {
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(132);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            bool degraded = i == 0 || i == 3;
            windows.Add(BiasStreams.WithWander(i, targets[i], degraded ? new PhysicalPoint(110, 0) : new PhysicalPoint(10, 0),
                degraded && wander ? new PhysicalPoint(15, 0) : null, 6, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Theory]
    [InlineData("radial", 0.10)]
    [InlineData("radial", 0.12)]
    [InlineData("ygrad", 0.10)]
    public void StreamG11_SteepRealField_FailsSafe(string kind, double gradient)
    {
        var targets = BiasStreams.Layout;
        var noise = new BiasStreams.Noise(111);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var bias = kind == "radial"
                ? new PhysicalPoint(gradient * (targets[i].X - (Screen.X / 2)), gradient * (targets[i].Y - (Screen.Y / 2)))
                : new PhysicalPoint(40 + (gradient * (targets[i].Y - (Screen.Y / 2))), 0);
            windows.Add(BiasStreams.Window(i, targets[i], bias, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Fact]
    public void StreamG11b_SteepFieldWithWanders_IsNeverNoTrigger()
    {
        var targets = BiasStreams.Layout;
        int centre = BiasStreams.CentreIndex();
        var noise = new BiasStreams.Noise(112);
        long t = 0;
        var windows = new List<GroundTruthWindow>();
        for (int i = 0; i < 5; i++)
        {
            var bias = new PhysicalPoint(0.10 * (targets[i].X - (Screen.X / 2)), 0.10 * (targets[i].Y - (Screen.Y / 2)));
            windows.Add(BiasStreams.WithWander(i, targets[i], bias,
                i == centre ? null : new PhysicalPoint(10, 5), 6, ref t, noise));
        }

        var decision = BiasDecisionRule.Evaluate(BiasEstimator.Measure(BiasStreams.Session(windows)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    // ── Round 3: near-threshold resolution ────────────────────────────────────

    [Fact]
    public void StreamG12_TrueSixtyTwoWithNoSecondPlace_Recalibrates()
    {
        var decision = BiasDecisionRule
            .Evaluate(BiasEstimator.Measure(BiasStreams.TwoPlaceField(trueX: 62, gap: 0)), Screen);

        Check.That(decision.Verdict).IsEqualTo(BiasDecisionRule.BiasVerdict.Recalibrate);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(55.0)]
    public void StreamG12_ASecondPlaceNearTheHold_NeverGivesNoTrigger(double gap)
    {
        // A true 62 px bias with an equal-sample second place gap px closer. The blend used to read
        // 47–60 px and every one of these said "No trigger" with nothing flagged. They are Inconclusive
        // now: the place linkage (28 px) keeps a 30 px gap's places distinct, where the ambiguity arm
        // sees both, and the δ = 10 guard band catches the 45–60 px readings.
        var decision = BiasDecisionRule
            .Evaluate(BiasEstimator.Measure(BiasStreams.TwoPlaceField(trueX: 62, gap)), Screen);

        Check.That(decision.Verdict).IsNotEqualTo(BiasDecisionRule.BiasVerdict.NoTrigger);
    }

    [Theory]
    [InlineData(7, 8, 5, 40.0, 120.0)]
    [InlineData(7, 8, 7, 60.0, 100.0)]
    public void Sweep_NearRest_StaysDecisiveWithoutWrongVerdicts(
        int cycles, int holdSamples, int restSamples, double radiusMin, double radiusMax)
    {
        // A user who finds steady fixation hard rests near the hold. On these shapes the filter that
        // counts a rival only when it fits within RivalResidualSlackPx of the winner keeps the
        // Inconclusive rate at or below the fixup's aim, with the correct No trigger still dominant and
        // never a Recalibrate.
        int noTrigger = 0, inconclusive = 0, recalibrate = 0;
        for (uint seed = 1; seed <= 100; seed++)
        {
            var decision = BiasDecisionRule.Evaluate(
                BiasEstimator.Measure(BiasStreams.NearRest(seed, cycles, holdSamples, restSamples, radiusMin, radiusMax)),
                Screen);
            switch (decision.Verdict)
            {
                case BiasDecisionRule.BiasVerdict.NoTrigger: noTrigger++; break;
                case BiasDecisionRule.BiasVerdict.Inconclusive: inconclusive++; break;
                default: recalibrate++; break;
            }
        }

        Check.That(recalibrate).IsEqualTo(0);
        // 35, not 25: at slack 10 the near-rest shapes are 31-32 % Inconclusive, and that is the price
        // of the shape B's R4-1 forbids (a confident false "No trigger" on 10–69 of 100 seeds).
        Check.That(inconclusive).IsLessOrEqualThan(35);
        Check.That(noTrigger).IsGreaterOrEqualThan(65);
    }

    // ── Decision rule unit tests (hand-built estimates) ───────────────────────

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
