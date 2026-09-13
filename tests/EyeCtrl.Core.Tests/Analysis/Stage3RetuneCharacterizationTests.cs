namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Benchmarks;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// What the Stage 3 One-Euro retune actually did, measured on the deterministic synthetic streams
/// through the Stage 2 analyzer: <see cref="ReplayPipeline.Baseline"/> (the frozen pre-retune filter) vs
/// <see cref="ReplayPipeline.Retuned"/>, same input, same metrics.
/// <para>
/// <b>These are NOT the acceptance gate.</b> The gate's criteria are defined against a scripted recording
/// of the user's own tracker, which does not exist yet — and two of its inputs (the raw per-axis sigma and
/// the fixation PSD) are also the debate's pre-registered kill conditions, so scoring the gate on
/// generator output would let a synthetic stream decide a question only real data can answer. The
/// generator's noise is white by construction, which is precisely the assumption under test. Ticket 5
/// owns the gate; this file owns the characterisation, and its assertions are direction-and-budget only.
/// </para>
/// <para>
/// The <c>Report</c> test prints the whole before/after table; run it with the xUnit v3 runner and
/// <c>-showliveoutput</c> to read it.
/// </para>
/// </summary>
public class Stage3RetuneCharacterizationTests
{
    private readonly ITestOutputHelper _output;

    public Stage3RetuneCharacterizationTests(ITestOutputHelper output) => _output = output;

    private static readonly PhysicalPoint FixationCentre = new(960, 540);

    // ── stability ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P95FixationDeviation_FallsSubstantially_OnTheNoisyStream()
    {
        // The headline stability number. The gate asks for >= 60 % on the real recording; this only
        // pins that the change moves it a long way in the right direction on a stream whose noise the
        // debate's model actually describes.
        var samples = GazeStreamGenerator.GenerateNoisyFixation();

        double before = Deviation(samples, ReplayPipeline.Baseline);
        double after = Deviation(samples, ReplayPipeline.Retuned);

        Check.That(after).IsStrictlyLessThan(before * 0.5);
    }

    [Fact]
    public void P95FixationDeviation_AlsoFalls_OnTheQuietDriftStream()
    {
        // The contrasting case. GenerateFixation is dominated by a 0.3 Hz drift circle — the shape the
        // debate's falsifier says a low-pass CANNOT help with — so the improvement here should be real
        // but much smaller than on the noisy stream. If it were equally large, the metric would be
        // measuring the filter's lag rather than its noise rejection.
        var samples = GazeStreamGenerator.GenerateFixation();

        double before = Deviation(samples, ReplayPipeline.Baseline);
        double after = Deviation(samples, ReplayPipeline.Retuned);

        Check.That(after).IsStrictlyLessThan(before);
    }

    // ── responsiveness ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepLatency_IsNowNonZero_ButWellInsideThe150msBudget()
    {
        // The recorded baseline was 0 ms — not a compliment but the debate's evidence that the shipped
        // filter was a pass-through on a saccade. The retune necessarily spends some of that, and the
        // gate REJECTS on latency regardless of how good the stability number looks, so the budget is
        // the assertion that matters.
        var samples = Step(FixationCentre, new PhysicalPoint(1360, 540), stepAt: 100);

        double? before = GazeMetrics.MeasureStepLatencyMs(
            ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Baseline), 100);
        double? after = GazeMetrics.MeasureStepLatencyMs(
            ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned), 100);

        Check.That(before).IsEqualTo(0.0);
        Check.That(after).IsNotNull();
        Check.That(after!.Value).IsStrictlyGreaterThan(0.0);
        Check.That(after.Value).IsLessOrEqualThan(150.0);
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(400.0)]
    [InlineData(900.0)]
    public void StepLatency_StaysInsideTheBudget_AtEverySaccadeSize(double stepPixels)
    {
        // One step size could be a lucky one. The jump detector's threshold is a function of the
        // measured noise, so a small step is the one most at risk of falling under it and being left
        // to the slow adaptive branch.
        var samples = Step(FixationCentre, new PhysicalPoint(FixationCentre.X + stepPixels, 540), stepAt: 100);

        double? latency = GazeMetrics.MeasureStepLatencyMs(
            ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned), 100);

        Check.That(latency).IsNotNull();
        Check.That(latency!.Value).IsLessOrEqualThan(150.0);
    }

    [Theory]
    [InlineData(10.0, 20.0)]
    [InlineData(10.0, 40.0)]
    [InlineData(10.0, 60.0)]
    [InlineData(10.0, 100.0)]
    [InlineData(15.0, 20.0)]
    [InlineData(15.0, 40.0)]
    [InlineData(15.0, 60.0)]
    [InlineData(15.0, 100.0)]
    public void StepLatency_OnANoisyStream_IsTheRegimeThatCanMissTheBudget(double sigma, double stepPixels)
    {
        // The gap the noiseless step tests could not see. On a clean stream the jump threshold pins at
        // its 15 px floor, so every step of interest clears it and takes the fast path. On a REAL stream
        // the threshold is ~3x the measured innovation RMS — roughly 44 px at sigma 10 and 67 px at
        // sigma 15 — so a step SMALLER than that is not a jump at all and is left to the adaptive
        // branch, which is exactly where the 150 ms budget is at risk. Every step tested here is a
        // realistic target-to-target distance.
        //
        // Ensemble mean over 40 seeds: a single noisy run's latency is a coin flip on where the noise
        // happens to sit at the 90 % crossing, and a per-seed assertion would be a flake generator.
        //
        // Deliberately NOT a budget assertion, even though most of these rows are over it. The measured
        // means are recorded as a gate risk on ticket 2 and the story; ticket 5 decides them on real
        // data, and a redesign is explicitly out of scope for this round. What is asserted is that the
        // response exists and is bounded — so the regime is pinned instead of unmeasured, and a change
        // that made it pathological would still fail here.
        double? mean = MeanStepLatencyMs(sigma, stepPixels, seeds: 40);

        Check.That(mean).IsNotNull();
        Check.That(mean!.Value).IsLessOrEqualThan(500.0);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(15.0)]
    public void StepLatency_OnANoisyStream_ImprovesAsTheStepClearsTheJumpThreshold(double sigma)
    {
        // The mechanism behind the rows above, pinned as an ordering so it cannot silently invert: a step
        // large enough to trip the detector resolves faster than one that is not. If this ever reverses,
        // the jump detector has stopped doing the job the whole retune rests on.
        double? large = MeanStepLatencyMs(sigma, 100.0, seeds: 40);
        double? small = MeanStepLatencyMs(sigma, 20.0, seeds: 40);

        Check.That(large).IsNotNull();
        Check.That(small).IsNotNull();
        Check.That(large!.Value).IsStrictlyLessThan(small!.Value);
    }

    // ── pops ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnIsolatedOutlier_IsAttenuatedButNotDestroyed()
    {
        // The honest limit of this ticket, pinned so it cannot be quietly forgotten: a one-pole filter
        // REDISTRIBUTES an outlier, it does not remove one. The retune shrinks the peak — the jump
        // detector's cap is still only ~68 % per sample where the baseline passed ~99 % — but the
        // excursion is still there. Destroying it needs the median prefilter (ticket 3), which is why
        // the story sequences the median FIRST and why this ticket must not ship alone if the real
        // recording shows impulsive pops.
        var samples = GazeStreamGenerator.GenerateFixation().ToArray();
        samples[400] = samples[400] with { X = samples[400].X + 200 };

        double basePeak = PeakAt(samples, ReplayPipeline.Baseline, 400);
        double retunedPeak = PeakAt(samples, ReplayPipeline.Retuned, 400);

        Check.That(retunedPeak).IsStrictlyLessThan(basePeak);
        Check.That(retunedPeak).IsStrictlyGreaterThan(40.0);   // still a pop by the analyzer's threshold

        static double PeakAt(GazePoint[] samples, ReplayPipeline.Candidate candidate, int index)
        {
            var frames = ReplayPipeline.RunSynthetic(samples, candidate);
            return frames[index].Filtered.DistanceTo(frames[index - 1].Filtered);
        }
    }

    [Fact]
    public void PopCount_IsNotWorsened_OnAnyGeneratorStream()
    {
        // The ticket's own acceptance wording. A filter that smeared each outlier across more samples
        // would raise the count even while lowering every peak.
        foreach (var (name, samples) in Streams())
        {
            int before = GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Baseline)).Count;
            int after = GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned)).Count;

            Check.WithCustomMessage($"pop count regressed on {name}").That(after).IsLessOrEqualThan(before);
        }
    }

    // ── dwell ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dwell_StillActivates_AndIsNoSlowerThanTheBaseline()
    {
        // The gate vetoes on dwell regression. Added smoothing lag could in principle push the gaze
        // point's arrival on target later; on a stream that starts already on target it must not.
        var samples = GazeStreamGenerator.GenerateNoisyFixation();

        var before = Dwell(samples, ReplayPipeline.Baseline);
        var after = Dwell(samples, ReplayPipeline.Retuned);

        Check.That(after.TimeToActivateMs).IsNotNull();
        Check.That(after.TimeToActivateMs!.Value).IsLessOrEqualThan(before.TimeToActivateMs!.Value + 50.0);
        Check.That(after.ResetsPerMinute).IsLessOrEqualThan(before.ResetsPerMinute);
    }

    [Theory]
    [InlineData(200.0)]
    [InlineData(600.0)]
    [InlineData(1200.0)]
    public void DwellAfterASaccade_CostsSomeTimeToActivate_AndTheCostIsMeasuredNotAssumed(double saccadePixels)
    {
        // The one place the retune plausibly makes things WORSE for the user, found by an App-side
        // wire-contract test that had been relying on the old filter's pass-through behaviour.
        //
        // Dwell starts when the FILTERED point enters the target. The shipped filter put it there on
        // the landing sample (that was the pathology); the retuned one converges over ~3 samples, so a
        // dwell that begins with a saccade starts its clock a little later. The gate's dwell criterion
        // allows +50 ms, and it is scored on a real recording — so this does NOT decide it. What this
        // pins is that the cost exists, is bounded by the same 150 ms the step-latency veto uses, and
        // grows with saccade size rather than being a fixed constant someone could forget about.
        var target = FixationCentre;
        var samples = Step(new PhysicalPoint(target.X - saccadePixels, target.Y), target, stepAt: 30, length: 120);

        double? before = Dwell(samples, ReplayPipeline.Baseline).TimeToActivateMs;
        double? after = Dwell(samples, ReplayPipeline.Retuned).TimeToActivateMs;

        Check.That(before).IsNotNull();
        Check.That(after).IsNotNull();
        Check.That(after!.Value - before!.Value).IsLessOrEqualThan(150.0);
    }

    // ── the report ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Report_PrintsTheBeforeAndAfterTable()
    {
        _output.WriteLine("stream                | metric            |     before |      after |   change");
        _output.WriteLine("----------------------+-------------------+------------+------------+---------");

        foreach (var (name, samples) in Streams())
        {
            Row(name, "p95 deviation px", Deviation(samples, ReplayPipeline.Baseline), Deviation(samples, ReplayPipeline.Retuned));
            Row(name, "rms deviation px",
                GazeMetrics.MeasureFixationDeviation(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Baseline)).RmsPixels,
                GazeMetrics.MeasureFixationDeviation(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned)).RmsPixels);
            Row(name, "pops (total)",
                GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Baseline)).Count,
                GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned)).Count);
            Row(name, "pops (isolated)",
                GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Baseline))
                    .Count(p => p.Duration == GazeMetrics.PopDuration.Isolated),
                GazeMetrics.DetectPops(ReplayPipeline.RunSynthetic(samples, ReplayPipeline.Retuned))
                    .Count(p => p.Duration == GazeMetrics.PopDuration.Isolated));

            var dwellBefore = Dwell(samples, ReplayPipeline.Baseline);
            var dwellAfter = Dwell(samples, ReplayPipeline.Retuned);
            Row(name, "dwell activate ms", dwellBefore.TimeToActivateMs ?? double.NaN, dwellAfter.TimeToActivateMs ?? double.NaN);
            Row(name, "dwell resets /min", dwellBefore.ResetsPerMinute, dwellAfter.ResetsPerMinute);
        }

        foreach (double size in new[] { 100.0, 400.0, 900.0 })
        {
            var step = Step(FixationCentre, new PhysicalPoint(FixationCentre.X + size, 540), stepAt: 100);
            Row($"step {size:0} px", "90 % latency ms",
                GazeMetrics.MeasureStepLatencyMs(ReplayPipeline.RunSynthetic(step, ReplayPipeline.Baseline), 100) ?? double.NaN,
                GazeMetrics.MeasureStepLatencyMs(ReplayPipeline.RunSynthetic(step, ReplayPipeline.Retuned), 100) ?? double.NaN);
        }

        foreach (double sigma in new[] { 10.0, 15.0 })
        {
            foreach (double step in new[] { 20.0, 40.0, 60.0, 100.0 })
            {
                double? mean = MeanStepLatencyMs(sigma, step, seeds: 40);
                _output.WriteLine(
                    $"{$"σ={sigma:0} step {step:0} px",-21} | {"90 % latency ms",-17} | {"—",10} | {mean ?? double.NaN,10:0.0} | " +
                    (mean is null or > 150.0 ? " OVER" : "    ok"));
            }
        }

        foreach (double saccade in new[] { 200.0, 600.0, 1200.0 })
        {
            var s = Step(new PhysicalPoint(FixationCentre.X - saccade, FixationCentre.Y), FixationCentre,
                stepAt: 30, length: 120);
            Row($"saccade {saccade:0} px", "dwell activate ms",
                Dwell(s, ReplayPipeline.Baseline).TimeToActivateMs ?? double.NaN,
                Dwell(s, ReplayPipeline.Retuned).TimeToActivateMs ?? double.NaN);
        }

        var outlier = GazeStreamGenerator.GenerateFixation().ToArray();
        outlier[400] = outlier[400] with { X = outlier[400].X + 200 };
        Row("200 px outlier", "output jump px",
            ReplayPipeline.RunSynthetic(outlier, ReplayPipeline.Baseline)[400].Filtered
                .DistanceTo(ReplayPipeline.RunSynthetic(outlier, ReplayPipeline.Baseline)[399].Filtered),
            ReplayPipeline.RunSynthetic(outlier, ReplayPipeline.Retuned)[400].Filtered
                .DistanceTo(ReplayPipeline.RunSynthetic(outlier, ReplayPipeline.Retuned)[399].Filtered));

        void Row(string stream, string metric, double before, double after)
        {
            string change = before == 0 || double.IsNaN(before) || double.IsNaN(after)
                ? "     —"
                : $"{(after - before) / before * 100.0,+6:0.0}%";
            _output.WriteLine($"{stream,-21} | {metric,-17} | {before,10:0.000} | {after,10:0.000} | {change}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Name, GazePoint[] Samples)> Streams() =>
    [
        ("NoisyFixation", GazeStreamGenerator.GenerateNoisyFixation()),
        ("Fixation", GazeStreamGenerator.GenerateFixation()),
        ("Saccade", GazeStreamGenerator.GenerateSaccade()),
        ("WithDropout", GazeStreamGenerator.GenerateWithDropout()),
    ];

    private static double Deviation(GazePoint[] samples, ReplayPipeline.Candidate candidate) =>
        GazeMetrics.MeasureFixationDeviation(ReplayPipeline.RunSynthetic(samples, candidate)).P95Pixels;

    private static GazeMetrics.ReplayDwell Dwell(GazePoint[] samples, ReplayPipeline.Candidate candidate) =>
        GazeMetrics.MeasureDwell(ReplayPipeline.RunSynthetic(samples, candidate), FixationCentre, 40, 600);

    private const int NoisyStepIndex = 60;
    private const int NoisyStepLength = 240;

    /// <summary>
    /// 90 % step latency read off the ENSEMBLE-MEAN trajectory over <paramref name="seeds"/> independent
    /// noise realisations, measured against the generator's known ground truth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT <see cref="GazeMetrics.MeasureStepLatencyMs"/>, and the reason is a limitation of
    /// that metric worth carrying to ticket 5. It takes both endpoints from single samples
    /// (<c>frames[stepIndex - 1].Filtered</c> and <c>frames[^1].Corrected</c>) and requires the output to
    /// reach 90 % and STAY there. All three are fine when the step dwarfs the noise — and degenerate when
    /// it does not: the endpoints are themselves noisy, and residual wobble keeps dropping the trajectory
    /// back out of a band that is only <c>0.1 × step</c> wide. Pointed at a 20 px step under sigma = 10 it
    /// returns ~3.5 SECONDS, which is the instrument reporting on itself.
    /// </para>
    /// <para>
    /// Averaging the trajectories first is the textbook answer: noise falls by sqrt(seeds) while the step
    /// response is unchanged, so what is left is the filter's actual response. Ground truth replaces the
    /// noisy endpoints for the same reason.
    /// </para>
    /// </remarks>
    private static double? MeanStepLatencyMs(double sigma, double stepPixels, int seeds)
    {
        var mean = new double[NoisyStepLength - NoisyStepIndex];
        for (int seed = 0; seed < seeds; seed++)
        {
            var frames = ReplayPipeline.RunSynthetic(NoisyStep(sigma, stepPixels, seed), ReplayPipeline.Retuned);
            for (int k = 0; k < mean.Length; k++)
                mean[k] += frames[NoisyStepIndex + k].Filtered.X - FixationCentre.X;
        }

        for (int k = 0; k < mean.Length; k++)
        {
            if (mean[k] / seeds >= 0.9 * stepPixels)
                return k * GazeStreamGenerator.SamplePeriodMicros / 1000.0;
        }
        return null;
    }

    /// <summary>A step of <paramref name="stepPixels"/> at sample 60, buried in per-axis Gaussian noise.</summary>
    private static GazePoint[] NoisyStep(double sigma, double stepPixels, int seed, int length = 240)
    {
        var rng = new Random(seed);
        var points = new GazePoint[length];
        for (int i = 0; i < length; i++)
        {
            double baseX = FixationCentre.X + (i < 60 ? 0 : stepPixels);
            points[i] = new GazePoint(
                baseX + (sigma * Gaussian(rng)),
                FixationCentre.Y + (sigma * Gaussian(rng)),
                i * GazeStreamGenerator.SamplePeriodMicros,
                IsValid: true);
        }
        return points;
    }

    /// <summary>Standard-normal sample via Box–Muller — mirrors <c>GazeStreamGenerator</c>'s.</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
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
