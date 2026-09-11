namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Benchmarks;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Validates the analyzer against the synthetic streams, so the instrument is trustworthy before any
/// real recording exists — the debate's sequencing (ticket 2 develops against the generator, ticket 3
/// supplies real data). Each test drives a stream whose answer is known by construction and checks the
/// metric recovers it.
/// </summary>
public class ReplayAnalyzerTests
{
    private const double SampleRateHz = 30.0;   // GazeStreamGenerator.SamplePeriodMicros

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void Replay_IsDeterministic_SameInputSameFrames()
    {
        // The whole instrument rests on this: if replay drifted between runs, a before/after comparison
        // of two candidates would be measuring the harness.
        var samples = GazeStreamGenerator.GenerateNoisyFixation();

        var a = ReplayPipeline.Run(samples, ReplayPipeline.Baseline);
        var b = ReplayPipeline.Run(samples, ReplayPipeline.Baseline);

        Check.That(a).HasSize(b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Check.That(a[i].Filtered.X).IsEqualTo(b[i].Filtered.X);
            Check.That(a[i].Filtered.Y).IsEqualTo(b[i].Filtered.Y);
            Check.That(a[i].IsInGap).IsEqualTo(b[i].IsInGap);
        }
    }

    [Fact]
    public void Metrics_AreDeterministic_SameInputSameNumbers()
    {
        var samples = GazeStreamGenerator.GenerateWithDropout();

        var first = Measure(samples);
        var second = Measure(samples);

        Check.That(first.P95DeviationPixels).IsEqualTo(second.P95DeviationPixels);
        Check.That(first.PopCount).IsEqualTo(second.PopCount);
        Check.That(first.DwellResetsPerMinute).IsEqualTo(second.DwellResetsPerMinute);
    }

    [Fact]
    public void Replay_DoesNotCarryFilterStateBetweenRuns()
    {
        // A shared filter instance would make the second run of an identical stream differ from the
        // first — the subtlest possible way for this harness to lie.
        var samples = GazeStreamGenerator.GenerateSaccade();

        var a = ReplayPipeline.Run(samples, ReplayPipeline.Baseline);
        ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Baseline);
        var b = ReplayPipeline.Run(samples, ReplayPipeline.Baseline);

        Check.That(a[0].Filtered.X).IsEqualTo(b[0].Filtered.X);
        Check.That(a[^1].Filtered.X).IsEqualTo(b[^1].Filtered.X);
    }

    // ── Stream shape ──────────────────────────────────────────────────────────

    [Fact]
    public void StreamShape_RecoversRateInvalidFractionAndGapCounts()
    {
        // GenerateWithDropout is built with exactly two dropouts: a 2-sample one (~66 ms, inside the
        // 150 ms budget) and a 6-sample one (~200 ms, past it).
        var samples = GazeStreamGenerator.GenerateWithDropout();

        var shape = GazeMetrics.MeasureStream(samples, maxGapMs: 150);

        Check.That(shape.SampleCount).IsEqualTo(1000);
        Check.That(shape.MedianDeltaMicroseconds).IsEqualTo(GazeStreamGenerator.SamplePeriodMicros);
        Check.That(shape.InvalidFraction).IsCloseTo(8.0 / 1000.0, 1e-9);
        Check.That(shape.ShortGapCount).IsEqualTo(1);
        Check.That(shape.LongGapCount).IsEqualTo(1);
    }

    [Fact]
    public void StreamShape_CountsALeadingDropout()
    {
        // A recording that STARTS invalid is the tracker not having acquired the eyes yet — the most
        // interesting dropout there is for acquisition analysis. It has no preceding valid sample, so
        // an implementation that spans from "the last valid one" silently reports zero gaps for it.
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        for (int i = 0; i < 10; i++)   // 10 samples ≈ 333 ms, past the 150 ms budget → a long gap
            samples[i] = samples[i] with { IsValid = false };

        var shape = GazeMetrics.MeasureStream(samples, maxGapMs: 150);

        Check.That(shape.LongGapCount).IsEqualTo(1);
        Check.That(shape.InvalidFraction).IsCloseTo(10.0 / 1000.0, 1e-9);
    }

    [Fact]
    public void StreamShape_CountsATrailingDropout()
    {
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        for (int i = samples.Length - 10; i < samples.Length; i++)
            samples[i] = samples[i] with { IsValid = false };

        var shape = GazeMetrics.MeasureStream(samples, maxGapMs: 150);

        Check.That(shape.LongGapCount).IsEqualTo(1);
    }

    [Fact]
    public void StreamShape_HistogramIsBucketedByMillisecond()
    {
        var shape = GazeMetrics.MeasureStream(GazeStreamGenerator.GenerateFixation());

        Check.That(shape.DeltaHistogram).HasSize(1);       // a perfectly regular synthetic stream
        Check.That(shape.DeltaHistogram[0].Milliseconds).IsEqualTo(33);
        Check.That(shape.DeltaHistogram[0].Count).IsEqualTo(999);
    }

    // ── Fixation deviation ────────────────────────────────────────────────────

    [Fact]
    public void FixationDeviation_IsLargerOnTheNoisierStream()
    {
        // NoisyFixation is ±15 px; Fixation is ±1.5 px plus an 8 px drift circle. The metric must order
        // them correctly or it is not measuring stability at all.
        var calm = Deviation(GazeStreamGenerator.GenerateFixation());
        var noisy = Deviation(GazeStreamGenerator.GenerateNoisyFixation());

        Check.That(noisy.P95Pixels).IsStrictlyGreaterThan(calm.P95Pixels);
        Check.That(noisy.RmsPixels).IsStrictlyGreaterThan(calm.RmsPixels);
    }

    [Fact]
    public void FixationDeviation_AgainstGroundTruth_RecoversTheKnownCentre()
    {
        // The generator holds NoisyFixation at (960, 540) with zero-mean noise, so deviation measured
        // against that exact point must land within a fraction of a pixel of the self-referential one.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateNoisyFixation(), ReplayPipeline.Unfiltered);

        var againstTruth = GazeMetrics.MeasureFixationDeviation(frames, new PhysicalPoint(960, 540));
        var againstCentroid = GazeMetrics.MeasureFixationDeviation(frames);

        Check.That(Math.Abs(againstTruth.RmsPixels - againstCentroid.RmsPixels)).IsStrictlyLessThan(1.0);
    }

    [Fact]
    public void FixationDeviation_ExcludesGapHeldSamples()
    {
        // A held point is frozen, so counting it would credit the pipeline with perfect stability
        // during a blink — flattering exactly the moment tracking is worst.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateWithDropout(), ReplayPipeline.Baseline);

        var measured = GazeMetrics.MeasureFixationDeviation(frames);

        Check.That(measured.SampleCount).IsStrictlyLessThan(frames.Length);
        Check.That(measured.SampleCount).IsEqualTo(frames.Count(f => !f.IsInGap));
    }

    [Fact]
    public void SegmentFixations_FindsOneSegmentPerSaccadeTarget()
    {
        // Five targets, so five dwelling segments — the free-view path, where there is no ground truth.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateSaccade(), ReplayPipeline.Unfiltered);

        var segments = GazeMetrics.SegmentFixations(frames, radiusPixels: 50, minSamples: 8);

        Check.That(segments).HasSize(5);
    }

    // ── Pops ──────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectPops_FindsAnInjectedIsolatedOutlier_AndCallsItSignal()
    {
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        samples[400] = samples[400] with { X = samples[400].X + 200 };   // one-sample 200 px excursion

        var frames = ReplayPipeline.Run(samples, ReplayPipeline.Unfiltered);
        var pops = GazeMetrics.DetectPops(frames, thresholdPixels: 40);

        Check.That(pops).HasSize(1);
        Check.That(pops[0].Duration).IsEqualTo(GazeMetrics.PopDuration.Isolated);
        Check.That(pops[0].Cause).IsEqualTo(GazeMetrics.PopCause.Signal);   // no dropout nearby
        Check.That(pops[0].PeakPixels).IsStrictlyGreaterThan(150.0);
    }

    [Fact]
    public void DetectPops_AttributesADropoutAdjacentExcursionToTheGapLane()
    {
        // The cross-tab's whole purpose: a median prefilter and the gap fixes must not both be able to
        // claim the same improvement.
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        samples[400] = samples[400] with { IsValid = false };
        samples[401] = samples[401] with { X = samples[401].X + 200 };   // recovery pop

        var frames = ReplayPipeline.Run(samples, ReplayPipeline.Unfiltered);
        var pops = GazeMetrics.DetectPops(frames, thresholdPixels: 40);

        Check.That(pops).Not.IsEmpty();
        Check.That(pops.Any(p => p.Cause == GazeMetrics.PopCause.Gap)).IsTrue();
    }

    [Fact]
    public void DetectPops_FindsNothingInACalmStream()
    {
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Unfiltered);

        Check.That(GazeMetrics.DetectPops(frames, thresholdPixels: 40)).IsEmpty();
    }

    [Fact]
    public void DetectPops_BucketsBySustainedWhenTheExcursionPersists()
    {
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        for (int i = 400; i < 410; i++)
            samples[i] = samples[i] with { X = samples[i].X + 200 };

        var frames = ReplayPipeline.Run(samples, ReplayPipeline.Unfiltered);
        var pops = GazeMetrics.DetectPops(frames, thresholdPixels: 40);

        Check.That(pops).Not.IsEmpty();
        Check.That(pops[0].Duration).IsEqualTo(GazeMetrics.PopDuration.Sustained);
    }

    // ── Step latency ──────────────────────────────────────────────────────────

    [Fact]
    public void StepLatency_IsZeroForTheUnfilteredPipeline()
    {
        // An identity filter reaches 100 % of the step on the step sample itself. Any non-zero answer
        // here would mean the latency metric is measuring the harness rather than the filter.
        var samples = Step(from: new PhysicalPoint(500, 500), to: new PhysicalPoint(900, 500), stepAt: 100);
        var frames = ReplayPipeline.Run(samples, ReplayPipeline.Unfiltered);

        Check.That(GazeMetrics.MeasureStepLatencyMs(frames, 100)).IsEqualTo(0.0);
    }

    [Fact]
    public void StepLatency_IsPositiveForAGenuinelySmoothingFilter()
    {
        // The metric's detector: an EMA at 0.15 needs several samples to cover 90 % of a step, so a
        // non-zero answer here proves the measurement can see latency at all — which is what makes the
        // zero measured for the shipped filter below a finding rather than a broken metric.
        var samples = Step(from: new PhysicalPoint(500, 500), to: new PhysicalPoint(900, 500), stepAt: 100);
        var smoothing = new ReplayPipeline.Candidate("EMA 0.15", () => new SmoothedFilter(0.15));
        var frames = ReplayPipeline.Run(samples, smoothing);

        var latency = GazeMetrics.MeasureStepLatencyMs(frames, 100);

        Check.That(latency).IsNotNull();
        Check.That(latency!.Value).IsStrictlyGreaterThan(0.0);
    }

    [Fact]
    public void StepLatency_IsZeroForTheShippedOneEuro_WhichIsTheDebatesPointAboutIt()
    {
        // MEASURED, not assumed: at the shipped minCutoff 5.0 / beta 0.5 the One-Euro filter covers 90 %
        // of a 400 px step within a single sample — it is effectively a pass-through on a saccade, which
        // is the debate's claim that the adaptive term hands the input straight back. Recorded here as a
        // baseline: if a retuned filter is ever proposed, this is the responsiveness it starts from and
        // the 150 ms ceiling is the budget it may spend.
        var samples = Step(from: new PhysicalPoint(500, 500), to: new PhysicalPoint(900, 500), stepAt: 100);
        var frames = ReplayPipeline.Run(samples, ReplayPipeline.Baseline);

        Check.That(GazeMetrics.MeasureStepLatencyMs(frames, 100)).IsEqualTo(0.0);
    }

    [Fact]
    public void StepLatency_IsNullWhenTheStreamNeverSettles()
    {
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateNoisyFixation(), ReplayPipeline.Baseline);

        // No step in this stream: "before" and the final raw point are the same place, so there is
        // nothing to measure — reported honestly as null rather than as a flattering zero.
        Check.That(GazeMetrics.MeasureStepLatencyMs(frames, 0)).IsNull();
    }

    // ── Dwell ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Dwell_ActivatesOnASteadyFixationOverTheTarget()
    {
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Baseline);

        var dwell = GazeMetrics.MeasureDwell(frames, new PhysicalPoint(960, 540), targetRadiusPixels: 40, dwellMs: 600);

        Check.That(dwell.TimeToActivateMs).IsNotNull();
        Check.That(dwell.Activations).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Dwell_NeverActivatesWhenGazeIsElsewhere()
    {
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Baseline);

        var dwell = GazeMetrics.MeasureDwell(frames, new PhysicalPoint(100, 100), targetRadiusPixels: 40);

        Check.That(dwell.TimeToActivateMs).IsNull();
        Check.That(dwell.Activations).IsEqualTo(0);
    }

    [Fact]
    public void Dwell_CountsProgressResets_WhenGazeEntersAndLeavesWithoutActivating()
    {
        // The positive control for the gate's dwell-hostility metric. 10 samples on target (333 ms,
        // short of the 600 ms dwell) then 10 away, repeatedly: it can never activate, so every leave is
        // a genuine lost dwell and the count must be strictly positive.
        //
        // Without this, reset counting could break entirely and go unnoticed: a `>= 0` assertion passes
        // when nothing is ever counted, and the gate's Reduction(0, 0) then reports "criterion not
        // exercised" as a PASS — a dead metric that still looks alive. The saccade stream cannot serve
        // as this control either: its cooldown-0 activations cancel the leaves via the
        // resets-minus-activations correction and legitimately yield zero.
        var target = new PhysicalPoint(960, 540);
        var frames = ReplayPipeline.Run(EnterLeaveStream(target, onTarget: 10, away: 10, cycles: 8),
                                        ReplayPipeline.Unfiltered);

        var dwell = GazeMetrics.MeasureDwell(frames, target, targetRadiusPixels: 40, dwellMs: 600);

        Check.That(dwell.Activations).IsEqualTo(0);            // never long enough to complete
        Check.That(dwell.ProgressResets).IsStrictlyGreaterThan(0);
        Check.That(dwell.ResetsPerMinute).IsStrictlyGreaterThan(0.0);
    }

    [Fact]
    public void Dwell_CountsNoResets_OnASteadyFixation()
    {
        // The negative half of the same control: a stream that never leaves the target must not
        // manufacture resets, or the metric would punish the very behaviour it rewards.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Baseline);

        var dwell = GazeMetrics.MeasureDwell(frames, new PhysicalPoint(960, 540), targetRadiusPixels: 40, dwellMs: 600);

        Check.That(dwell.Activations).IsStrictlyGreaterThan(0);
        Check.That(dwell.ProgressResets).IsEqualTo(0);
    }

    [Fact]
    public void Gate_RejectsACandidateThatDoesNotReduceDwellResets()
    {
        // The metric reaching the gate: a candidate that leaves dwell just as hostile fails, even with
        // every other criterion comfortably met.
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("still-hostile", 1, 0, 100, 700, 9);   // 10 % down, needs 80

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsFalse();
        Check.That(result.Verdicts.Single(v => v.Criterion.Contains("resets per minute")).Passed).IsFalse();
    }

    // ── Spectrum ──────────────────────────────────────────────────────────────

    [Fact]
    public void Psd_PutsMostPowerBelow2Hz_ForTheSlowDriftStream()
    {
        // GenerateFixation's dominant component is a 0.3 Hz drift circle. If a real recording looks
        // like this, Signal's falsifier fires and the filter stage is withdrawn — so the metric has to
        // identify the case unambiguously.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateFixation(), ReplayPipeline.Unfiltered);

        var psd = GazeMetrics.ComputePsd(frames, SampleRateHz);

        Check.That(GazeMetrics.PowerFractionBelow(psd, 2.0)).IsStrictlyGreaterThan(0.5);
    }

    [Fact]
    public void Psd_SpreadsPowerAcrossTheBandForBroadbandNoise()
    {
        // NoisyFixation is near-white ±15 px: the contrasting case, where a low-pass has something to
        // remove. Without this pairing the test above would pass on any stream at all.
        var frames = ReplayPipeline.Run(GazeStreamGenerator.GenerateNoisyFixation(), ReplayPipeline.Unfiltered);

        var psd = GazeMetrics.ComputePsd(frames, SampleRateHz);

        Check.That(GazeMetrics.PowerFractionBelow(psd, 2.0)).IsStrictlyLessThan(0.5);
    }

    // ── Acceptance gate ───────────────────────────────────────────────────────

    [Fact]
    public void Gate_AcceptsACandidateThatMeetsEveryCriterion()
    {
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("candidate", 10, 10, 120, 720, 1);

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsTrue();
        Check.That(result.Verdicts.All(v => v.Passed)).IsTrue();
    }

    [Fact]
    public void Gate_RejectsOnLatency_EvenWithAPerfectDeviationNumber()
    {
        // The debate's explicit rule: reject on latency or dwell regression regardless of the
        // deviation number.
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("slow", 1, 0, 400, 700, 0);

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsFalse();
        var latency = result.Verdicts.Single(v => v.Criterion.Contains("step latency"));
        Check.That(latency.Passed).IsFalse();
        Check.That(latency.IsVeto).IsTrue();
    }

    [Fact]
    public void Gate_RejectsOnDwellRegression_EvenWithAPerfectDeviationNumber()
    {
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("dwell-hostile", 1, 0, 100, 900, 0);

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsFalse();
        Check.That(result.Verdicts.Single(v => v.Criterion.Contains("time-to-activate")).Passed).IsFalse();
    }

    [Fact]
    public void Gate_RejectsAnUnmeasurableVeto_RatherThanWaivingIt()
    {
        // A candidate whose latency could not be measured has not demonstrated it is within budget.
        // Silently passing would turn the veto into decoration.
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("unmeasured", 1, 0, null, 700, 0);

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsFalse();
    }

    [Fact]
    public void Gate_RejectsAnInsufficientDeviationImprovement()
    {
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("marginal", 25, 10, 100, 700, 1);   // 37.5 % down, needs 60

        var result = AcceptanceGate.Evaluate(baseline, candidate);

        Check.That(result.Accepted).IsFalse();
    }

    [Fact]
    public void Gate_Report_NamesEveryCriterionAndTheDecision()
    {
        var baseline = new AcceptanceGate.Measurement("baseline", 40, 100, 20, 700, 10);
        var candidate = new AcceptanceGate.Measurement("slow", 1, 0, 400, 700, 0);

        string report = AcceptanceGate.Evaluate(baseline, candidate).ToReport();

        Check.That(report).Contains("REJECTED");
        Check.That(report).Contains("veto");
        Check.That(report).Contains("p95 radial fixation deviation");
        Check.That(report).Contains("dwell-progress resets per minute");
    }

    [Fact]
    public void Gate_RunsEndToEndOverTheSyntheticBaseline()
    {
        // The instrument working as a whole: two real candidate pipelines measured on one stream and
        // fed through the gate. The verdict is not asserted — the point is that a full measurement
        // round-trip produces a complete, readable report with every criterion decided.
        var samples = GazeStreamGenerator.GenerateNoisyFixation();

        var result = AcceptanceGate.Evaluate(
            Measure(samples, ReplayPipeline.Unfiltered, "unfiltered"),
            Measure(samples, ReplayPipeline.Baseline, "baseline OneEuro"));

        Check.That(result.Verdicts).HasSize(5);
        Check.That(result.ToReport()).Contains("Acceptance gate");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static GazeMetrics.FixationDeviation Deviation(GazePoint[] samples) =>
        GazeMetrics.MeasureFixationDeviation(ReplayPipeline.Run(samples, ReplayPipeline.Unfiltered));

    private static AcceptanceGate.Measurement Measure(
        GazePoint[] samples, ReplayPipeline.Candidate? candidate = null, string? name = null)
    {
        var used = candidate ?? ReplayPipeline.Baseline;
        var frames = ReplayPipeline.Run(samples, used);
        var deviation = GazeMetrics.MeasureFixationDeviation(frames);
        var dwell = GazeMetrics.MeasureDwell(frames, new PhysicalPoint(960, 540), 40, 600);

        return new AcceptanceGate.Measurement(
            name ?? used.Name,
            deviation.P95Pixels,
            GazeMetrics.DetectPops(frames).Count,
            GazeMetrics.MeasureStepLatencyMs(frames, 1),
            dwell.TimeToActivateMs,
            dwell.ResetsPerMinute);
    }

    /// <summary>
    /// Alternates <paramref name="onTarget"/> samples on <paramref name="target"/> with
    /// <paramref name="away"/> samples far from it. With <c>onTarget</c> short of the dwell time this
    /// never activates, so every cycle contributes exactly one lost dwell.
    /// </summary>
    private static GazePoint[] EnterLeaveStream(PhysicalPoint target, int onTarget, int away, int cycles)
    {
        var points = new List<GazePoint>((onTarget + away) * cycles);
        long t = 0;
        for (int c = 0; c < cycles; c++)
        {
            for (int i = 0; i < onTarget; i++, t += GazeStreamGenerator.SamplePeriodMicros)
                points.Add(new GazePoint(target.X, target.Y, t, IsValid: true));
            for (int i = 0; i < away; i++, t += GazeStreamGenerator.SamplePeriodMicros)
                points.Add(new GazePoint(target.X + 600, target.Y + 400, t, IsValid: true));
        }
        return points.ToArray();
    }

    /// <summary>A clean step: <paramref name="stepAt"/> samples at one point, then the rest at another.</summary>
    private static GazePoint[] Step(PhysicalPoint from, PhysicalPoint to, int stepAt, int length = 300)
    {
        var points = new GazePoint[length];
        for (int i = 0; i < length; i++)
        {
            var p = i < stepAt ? from : to;
            points[i] = new GazePoint(p.X, p.Y, i * GazeStreamGenerator.SamplePeriodMicros, IsValid: true);
        }
        return points;
    }
}
