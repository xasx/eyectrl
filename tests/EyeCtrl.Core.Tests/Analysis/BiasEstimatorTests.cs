namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// The bias probe's estimator, validated against a stream whose answer is known by construction —
/// the same discipline ticket 2's gates follow. The acceptance stream carries exactly the trap the
/// skeptic named: a distractor cluster LARGER than the target's, which a largest-cluster pick alone
/// would follow, and nearer to the target than the true hold, which a nearest-to-target tie-break
/// would follow. Only "largest cluster whose bias vector agrees with the other targets" survives both.
/// <para>
/// All constants here are the debate's, not tuned to make the tests pass: bias 300 px, σ 9 px, a
/// 250 px glance, and the real five-point target layout for the user's 3840×2400 display.
/// </para>
/// </summary>
public class BiasEstimatorTests
{
    private static readonly PhysicalPoint Screen = new(3840, 2400);

    /// <summary>The true bias every target is held at — the number the estimator must recover.</summary>
    private static readonly PhysicalPoint TrueBias = new(300, 0);

    private const double GlanceRadiusPx = 250;
    private const double NoiseSigmaPx = 9;
    private const long StepUs = 30_000;     // the tracker's ~33 Hz
    private const int HoldSamples = 6;
    private const int GlanceSamples = 7;    // larger than the hold, deliberately
    private const int Cycles = 10;

    /// <summary>
    /// The five-point probe layout, one window per target, each alternating a hold at the true bias
    /// with a glance 250 px away in a per-target direction.
    /// </summary>
    private static GroundTruthSession BuildAcceptanceSession()
    {
        var targets = CalibrationTargetPositionHelper.GetPositions(5, Screen);
        var noise = new Noise(seed: 20260926);
        var windows = new List<GroundTruthWindow>(targets.Count);

        // Directions must keep each glance at least ~200 px from the hold (a dispersion of 150 would
        // otherwise MERGE the two into one cluster straddling both), stay 250 px from the target so a
        // nearest-to-target rule still prefers them, and differ enough that no two glance vectors —
        // or a glance and the true bias — agree within the tolerance.
        double[] glanceDegrees = [45, 90, 135, 225, 270];

        long t = 0;
        for (int id = 0; id < targets.Count; id++)
        {
            var target = targets[id];
            double radians = glanceDegrees[id] * Math.PI / 180.0;
            var glance = new PhysicalPoint(
                target.X + (GlanceRadiusPx * Math.Cos(radians)),
                target.Y + (GlanceRadiusPx * Math.Sin(radians)));

            long displayedFrom = t;
            var samples = new List<GroundTruthSample>();
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                for (int i = 0; i < HoldSamples; i++, t += StepUs)
                {
                    samples.Add(new GroundTruthSample(
                        target.X + TrueBias.X + (noise.NextGaussian() * NoiseSigmaPx),
                        target.Y + TrueBias.Y + (noise.NextGaussian() * NoiseSigmaPx),
                        t, Valid: true));
                }

                for (int i = 0; i < GlanceSamples; i++, t += StepUs)
                {
                    samples.Add(new GroundTruthSample(
                        glance.X + (noise.NextGaussian() * NoiseSigmaPx),
                        glance.Y + (noise.NextGaussian() * NoiseSigmaPx),
                        t, Valid: true));
                }
            }

            windows.Add(new GroundTruthWindow(
                id, target.X, target.Y, displayedFrom, displayedFrom, t - StepUs, samples.Count)
            {
                Samples = samples,
            });
        }

        return new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion, "StreamEngine",
            Screen.X, Screen.Y, SettleWindowMs: 300, windows);
    }

    // ── Acceptance ────────────────────────────────────────────────────────────

    [Fact]
    public void Acceptance_RecoversB300_DespiteALargerAndNearerDistractor()
    {
        var estimate = BiasEstimator.Measure(BuildAcceptanceSession());

        Check.That(estimate.Bias).IsNotNull();
        Check.That(estimate.Bias!.Value.X).IsCloseTo(TrueBias.X, 20);
        Check.That(estimate.Bias!.Value.Y).IsCloseTo(TrueBias.Y, 20);
        Check.That(estimate.TargetsUsed).IsEqualTo(5);

        // Every chosen probe logs the cluster size and the distance — the debate requires both,
        // because they are what says whether the estimate rests on real holds or on scraps.
        foreach (var probe in estimate.Probes)
        {
            Check.That(probe.Chosen).IsNotNull();
            Check.That(probe.Chosen!.SampleCount).IsGreaterOrEqualThan(BiasEstimator.MinClusterSamples);
            Check.That(probe.DistancePx!.Value).IsCloseTo(300, 20);
        }

        var report = estimate.Describe();
        Check.That(report).Contains("cluster 6 samples");     // the holds, not the 7-sample glances
        Check.That(report).Contains("distance 300.");         // logged per probe
    }

    [Fact]
    public void Acceptance_TheStreamReallyDoesCarryALargerDistractor()
    {
        // The guard on the test above: if a future edit shrank the glances, "largest cluster" would
        // pass for the wrong reason and the acceptance test would stop testing anything.
        var estimate = BiasEstimator.Measure(BuildAcceptanceSession());

        bool someWindowHasABiggerClusterThanItsChoice = estimate.Probes.Any(p =>
            p.Chosen is not null && p.Clusters.Any(c => c.SampleCount > p.Chosen.SampleCount));

        Check.That(someWindowHasABiggerClusterThanItsChoice).IsTrue();
    }

    [Fact]
    public void NoVectorConsistentAcrossTargets_MeansNoEstimate()
    {
        // Two targets, biases 90° apart: no candidate vector is supported by both, and one target
        // alone cannot be "consistent with the other targets". Reported as no estimate rather than as
        // a confident average of two contradictory holds.
        var targets = CalibrationTargetPositionHelper.GetPositions(5, Screen);
        var session = new GroundTruthSession(
            GroundTruthSession.CurrentSchemaVersion, "StreamEngine", Screen.X, Screen.Y, 300,
            [
                BuildWindow(0, targets[0], new PhysicalPoint(60, 0)),
                BuildWindow(1, targets[1], new PhysicalPoint(0, 60)),
            ]);

        var estimate = BiasEstimator.Measure(session);

        Check.That(estimate.Bias).IsNull();
        Check.That(estimate.TargetsUsed).IsEqualTo(0);
        Check.That(estimate.Describe()).Contains("no estimate");
    }

    [Fact]
    public void ClustersStartingInsideTheOnsetGuard_AreIgnored()
    {
        // The first 300 ms after a target appears are the saccade and the search, not a hold. Samples
        // there must not become clusters even when they are perfectly steady — a dense synthetic
        // stream can put a whole pre-guard fixation inside the window and would otherwise measure it.
        var target = new PhysicalPoint(500, 500);
        var samples = new List<GroundTruthSample>();
        for (int i = 0; i < 10; i++)      // 0–270 ms, all before the guard
            samples.Add(new GroundTruthSample(target.X - 100, target.Y, i * StepUs, Valid: true));
        for (int i = 10; i < 30; i++)     // 300 ms onward: eligible, held 100 px to the right
            samples.Add(new GroundTruthSample(target.X + 100, target.Y, i * StepUs, Valid: true));

        var window = new GroundTruthWindow(0, target.X, target.Y, 0, 0, 29 * StepUs, samples.Count)
        {
            Samples = samples,
        };

        var clusters = BiasEstimator.ExtractClusters(window);

        Check.That(clusters).HasSize(1);
        Check.That(clusters[0].Centroid.X).IsCloseTo(target.X + 100, 1e-9);
        Check.That(clusters[0].StartUs).IsGreaterOrEqualThan(300_000L);
    }

    private static GroundTruthWindow BuildWindow(int id, PhysicalPoint target, PhysicalPoint bias)
    {
        var samples = new List<GroundTruthSample>();
        long t = 0;
        for (int i = 0; i < 40; i++, t += StepUs)
            samples.Add(new GroundTruthSample(target.X + bias.X, target.Y + bias.Y, t, Valid: true));
        return new GroundTruthWindow(id, target.X, target.Y, 0, 0, t - StepUs, samples.Count)
        {
            Samples = samples,
        };
    }

    // ── Decision rule ─────────────────────────────────────────────────────────

    [Fact]
    public void Decision_TriggersWhenTwoTargetsAreAtSixtyPixels()
    {
        var estimate = BuildEstimate(
            new PhysicalPoint(65, 0), new PhysicalPoint(62, 0),
            new PhysicalPoint(10, 0), new PhysicalPoint(10, 0), new PhysicalPoint(10, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.ShouldRecalibrate).IsTrue();
        Check.That(decision.Reason).Contains("Tobii Dynavox Eye Tracking Settings");
    }

    [Fact]
    public void Decision_TriggersOnTheCentreAloneAtSeventyFivePixels()
    {
        // The centre target is the one nearest the screen centre; 75 px is R75, the fixation radius.
        var estimate = BuildEstimate(
            new PhysicalPoint(5, 0), new PhysicalPoint(5, 0),
            new PhysicalPoint(80, 0), new PhysicalPoint(5, 0), new PhysicalPoint(5, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.ShouldRecalibrate).IsTrue();
        Check.That(decision.CentreMagnitudePx!.Value).IsEqualTo(80.0);
    }

    [Fact]
    public void Decision_DoesNotTrigger_BelowBothArms()
    {
        // Index 2 is the centre target for the five-point layout. 70 px is inside R75 and only one
        // target is at/above 60, so neither arm fires: 70 >= 60 is true, but the per-target arm needs
        // two, and the centre arm needs 75.
        var estimate = BuildEstimate(
            new PhysicalPoint(5, 0), new PhysicalPoint(5, 0),
            new PhysicalPoint(70, 0), new PhysicalPoint(5, 0), new PhysicalPoint(5, 0));

        var decision = BiasDecisionRule.Evaluate(estimate, Screen);

        Check.That(decision.ShouldRecalibrate).IsFalse();
        Check.That(decision.Reason).Contains("No trigger");
    }

    /// <summary>Builds an estimate whose five probes were all measured at the given magnitudes.</summary>
    private static BiasEstimator.Estimate BuildEstimate(params PhysicalPoint[] perTargetBias)
    {
        var targets = CalibrationTargetPositionHelper.GetPositions(5, Screen);
        var probes = new List<BiasEstimator.Probe>(perTargetBias.Length);
        for (int i = 0; i < perTargetBias.Length; i++)
        {
            var target = targets[i];
            var centroid = new PhysicalPoint(target.X + perTargetBias[i].X, target.Y + perTargetBias[i].Y);
            var cluster = new BiasEstimator.Cluster(centroid, 50, 0, 1_000_000);
            probes.Add(new BiasEstimator.Probe(i, target, [cluster], cluster));
        }
        return new BiasEstimator.Estimate(new PhysicalPoint(0, 0), probes.Count, probes.Count, probes);
    }

    /// <summary>Deterministic Gaussian noise (LCG + Box–Muller), so the stream is identical every run.</summary>
    private sealed class Noise(uint seed)
    {
        private uint _state = seed;

        private double NextUnit()
        {
            _state = (1664525u * _state) + 1013904223u;
            return (_state >> 8) / (double)(1 << 24);
        }

        public double NextGaussian()
        {
            double u1 = Math.Max(NextUnit(), double.Epsilon);
            double u2 = NextUnit();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
