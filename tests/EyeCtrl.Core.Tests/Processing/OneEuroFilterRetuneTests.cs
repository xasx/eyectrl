namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The four Stage 3 changes to <see cref="OneEuroFilter"/>, each pinned by the property that would be
/// silently lost if it were reverted: the filtered-derivative speed estimate, the innovation jump
/// detector, the <c>sampleRate/3</c> cutoff cap, and the <c>dt</c> clamp that must NOT reach the
/// derivative divisor.
/// <para>
/// <see cref="OneEuroFilterTests"/> keeps the pre-existing behavioural pins (first-point passthrough,
/// convergence, "past halfway in one sample", invalid-hold). Those still pass unchanged and are the
/// reason the jump detector's threshold is where it is.
/// </para>
/// </summary>
public class OneEuroFilterRetuneTests
{
    private const long Period = 33_000;          // ~30 Hz, the tracker's rate
    private const double PeriodSeconds = 0.033;

    // ── (1) the speed estimate comes from the FILTERED signal ────────────────────────────────────

    [Theory]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void Attenuation_IsIndependentOfHowNoisyTheInputIs(double sigma)
    {
        // THE pathology the retune exists to remove, stated as the debate states it: with the old
        // estimator, "attenuation is a monotonically decreasing function of input noise" — the noisier
        // the tracker, the less the filter filtered, because the speed term was reading its own noise.
        // Measured here head to head against the frozen pre-retune implementation at the same sigma.
        //
        // The retuned filter must hold a roughly constant attenuation across a 7x range of input noise;
        // the legacy one collapses from mildly useful to nearly nothing over the same range. The
        // relation between the two is what matters and it is asserted as a relation, so neither number
        // has to be hardcoded.
        double retuned = Attenuation(new OneEuroFilter(), sigma);
        double legacy = Attenuation(new Analysis.LegacyOneEuroFilter(), sigma);

        Check.That(retuned).IsStrictlyGreaterThan(0.55);
        Check.That(retuned).IsStrictlyGreaterThan(legacy * 2.0);
    }

    [Fact]
    public void Attenuation_IsNotBoughtByASilentlyRisingCutoff()
    {
        // The mechanism behind the test above, so a future change cannot keep the attenuation number
        // while losing the property. The resting cutoff must stay near minCutoff however noisy the
        // input is — the legacy estimator's ran to tens or hundreds of Hz. A little movement is
        // expected and honest: the FILTERED signal still wanders, so its derivative is not exactly
        // zero. It is bounded, which is the whole difference.
        double clean = RestingCutoff(0.0);
        double noisy = RestingCutoff(12.0);

        Check.That(clean).IsCloseTo(1.5, 1e-9);
        Check.That(noisy - clean).IsStrictlyLessThan(1.0);

        static double RestingCutoff(double noisePixels)
        {
            var filter = new OneEuroFilter();
            var rng = new Random(1234);
            long ts = 0;
            for (int i = 0; i < 300; i++, ts += Period)
            {
                double dx = noisePixels * Gaussian(rng);
                double dy = noisePixels * Gaussian(rng);
                filter.Filter(new GazePoint(960 + dx, 540 + dy, ts, true));
            }
            return filter.LastCutoffHz;
        }
    }

    /// <summary>
    /// Fraction of the input's radial deviation about a stationary target that the filter removes:
    /// <c>1 − RMS(output − centre) / RMS(input − centre)</c>. The first 50 samples are dropped so the
    /// filter's convergence onto the target is not scored as noise.
    /// </summary>
    private static double Attenuation(Interfaces.IGazeFilter filter, double sigma, int samples = 2000)
    {
        var rng = new Random(20260913);
        const double centreX = 960, centreY = 540;
        long ts = 0;
        double inSum = 0, outSum = 0;
        int counted = 0;

        for (int i = 0; i < samples; i++, ts += Period)
        {
            double x = centreX + (sigma * Gaussian(rng));
            double y = centreY + (sigma * Gaussian(rng));
            var f = filter.Filter(new GazePoint(x, y, ts, true));
            if (i < 50) continue;

            inSum += ((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY));
            outSum += ((f.X - centreX) * (f.X - centreX)) + ((f.Y - centreY) * (f.Y - centreY));
            counted++;
        }

        return 1.0 - Math.Sqrt(outSum / counted) / Math.Sqrt(inSum / counted);
    }

    [Fact]
    public void RestingCutoff_OnAStationaryTarget_IsTheConfiguredMinimum()
    {
        // The other half of the same claim, absolutely rather than differentially: with nothing moving,
        // the adaptive term must contribute nothing at all.
        var filter = new OneEuroFilter(minCutoff: 1.5, beta: 20.0);
        long ts = 0;
        for (int i = 0; i < 100; i++, ts += Period)
            filter.Filter(new GazePoint(960, 540, ts, true));

        Check.That(filter.LastCutoffHz).IsCloseTo(1.5, 1e-9);
    }

    // ── beta's units ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Beta_IsHzPerScreenDiagonalPerSecond()
    {
        // The rescale, pinned as an equation rather than as a number: at a steady V px/s the cutoff
        // must settle at minCutoff + beta * V / SpeedNormalizationPixels. A revert to pixel units
        // would overshoot this by a factor of 2000 and be capped instead.
        //
        // 200 px/s (6.6 px per sample) is deliberately below the jump detector's floor, so this
        // measures the adaptive branch and not the detector.
        const double velocity = 200.0;
        const double minCutoff = 1.5;
        const double beta = 60.0;
        var filter = new OneEuroFilter(minCutoff, beta);

        long ts = 0;
        for (int i = 0; i < 200; i++, ts += Period)
            filter.Filter(new GazePoint(200 + (velocity * PeriodSeconds * i), 540, ts, true));

        double expected = minCutoff + (beta * velocity / OneEuroFilter.SpeedNormalizationPixels);
        Check.That(expected).IsCloseTo(7.5, 1e-9);          // the arithmetic, stated once
        Check.That(filter.LastCutoffHz).IsCloseTo(expected, 0.2);
        Check.That(filter.LastSampleWasJump).IsFalse();      // …and it really was the adaptive branch
    }

    // ── (2) the jump detector ────────────────────────────────────────────────────────────────────

    [Fact]
    public void JumpDetector_FiresOnTheVeryFirstSampleOfASaccade()
    {
        // The whole reason it exists. beta responds only once the derivative EMA has charged, which
        // takes several samples; the innovation is available immediately. Pinned on the FIRST stepped
        // sample, and paired with the sample before it so "always fires" cannot pass.
        var filter = new OneEuroFilter();
        long ts = 0;
        for (int i = 0; i < 30; i++, ts += Period)
            filter.Filter(new GazePoint(500, 500, ts, true));

        Check.That(filter.LastSampleWasJump).IsFalse();      // steady state: quiet

        var afterStep = filter.Filter(new GazePoint(900, 500, ts, true));

        Check.That(filter.LastSampleWasJump).IsTrue();
        Check.That(afterStep.X).IsStrictlyGreaterThan(700.0); // and it really opened the filter
    }

    [Fact]
    public void JumpDetector_StaysQuietThroughOrdinaryFixationNoise()
    {
        // The cost side. A detector that fired on noise would hand the noise straight through and undo
        // the filter — so on a sigma ≈ 10 px stationary target it must be essentially silent. The
        // threshold self-tunes to ~4.2 sigma, so the expected count over 1000 samples is well under 1.
        var filter = new OneEuroFilter();
        var rng = new Random(4242);
        long ts = 0;
        int fired = 0;

        for (int i = 0; i < 1000; i++, ts += Period)
        {
            filter.Filter(new GazePoint(960 + (10.0 * Gaussian(rng)), 540 + (10.0 * Gaussian(rng)), ts, true));
            if (filter.LastSampleWasJump) fired++;
        }

        Check.That(fired).IsStrictlyLessThan(10);   // 1 % of the stream; observed 0 at this seed
    }

    [Fact]
    public void JumpThreshold_IsUsableImmediatelyAfterAReset_NotAfterTwentySamples()
    {
        // GapInterpolator resets the filter after every dropout past the budget, so "the first twenty
        // samples" is not a rare startup case — it is the window right after every longer blink, which
        // is exactly where the recovery pop lives.
        //
        // With the estimator starting empty the threshold sits at its floor, and on a sigma = 10 px
        // stream roughly a third of samples clear a 15 px floor. Seeding it as a running mean (weight
        // 1/n while young) means ONE quiet sample already produces a usable threshold. Counted over the
        // first 20 samples only, so a plain EMA from zero cannot pass by converging later.
        var filter = new OneEuroFilter();
        var rng = new Random(31337);
        long ts = 0;
        int fired = 0;

        for (int i = 0; i < 20; i++, ts += Period)
        {
            filter.Filter(new GazePoint(960 + (10.0 * Gaussian(rng)), 540 + (10.0 * Gaussian(rng)), ts, true));
            if (filter.LastSampleWasJump) fired++;
        }

        Check.That(fired).IsLessOrEqualThan(2);
    }

    [Fact]
    public void JumpThreshold_ScalesWithTheMeasuredNoise_NotAFixedConstant()
    {
        // A fixed threshold is right for exactly one tracker. This pins that the threshold tracks the
        // signal: a noisier stream must raise its own bar, which is what keeps the false-fire rate
        // constant across devices, calibrations and sample rates.
        Check.That(ThresholdAfterNoise(20.0)).IsStrictlyGreaterThan(ThresholdAfterNoise(4.0));

        static double ThresholdAfterNoise(double sigma)
        {
            var filter = new OneEuroFilter();
            var rng = new Random(99);
            long ts = 0;
            for (int i = 0; i < 400; i++, ts += Period)
                filter.Filter(new GazePoint(960 + (sigma * Gaussian(rng)), 540 + (sigma * Gaussian(rng)), ts, true));
            return filter.LastJumpThresholdPixels;
        }
    }

    // ── (3) the cutoff cap ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AdaptiveCutoff_NeverExceedsTheSampleRateCap_AndTheCapReallyEngages()
    {
        // Without the cap the adaptive branch degrades into DirectFilter: above Nyquist the cutoff is
        // meaningless and alpha -> 1. beta is at the top of its range, so the uncapped formula asks for
        // far more than the ceiling well before the movement gets fast.
        //
        // The stimulus ACCELERATES (0 -> ~1000 px/s over 150 samples) rather than starting at speed.
        // A constant-velocity ramp that begins abruptly puts every sample over the jump threshold — the
        // per-sample displacement is itself the innovation — so the estimator never gets a quiet sample,
        // the detector latches on, and the adaptive branch under test is never reached at all. That is
        // real behaviour (see the class remarks), but it makes for a vacuous cap test: an earlier
        // revision of this test used a 700 px/s constant ramp and passed with the cap deleted.
        var filter = new OneEuroFilter(minCutoff: 1.5, beta: 60.0);
        const double acceleration = 200.0;   // px/s^2
        long ts = 0;
        int capped = 0;
        double peakSpeedDrivenCutoff = 0;

        for (int i = 0; i < 150; i++, ts += Period)
        {
            double t = i * PeriodSeconds;
            filter.Filter(new GazePoint(100 + (0.5 * acceleration * t * t), 540, ts, true));
            if (filter.LastCutoffCapHz <= 0) continue;   // the initialising sample sets neither

            Check.That(filter.LastCutoffHz).IsLessOrEqualThan(filter.LastCutoffCapHz);
            if (!filter.LastSampleWasJump && filter.LastCutoffHz == filter.LastCutoffCapHz)
            {
                capped++;
                peakSpeedDrivenCutoff = filter.LastCutoffCapHz;
            }
        }

        // Not vacuous on either side: the ceiling really bound, on the ADAPTIVE branch, many times.
        Check.That(capped).IsStrictlyGreaterThan(20);
        Check.That(peakSpeedDrivenCutoff).IsCloseTo(1.0 / (3.0 * PeriodSeconds), 1e-9);
    }

    [Fact]
    public void SustainedFastMotionThatStartsAbruptly_RunsAtTheCapForItsWholeDuration()
    {
        // The behaviour the test above had to design around, pinned rather than left as folklore.
        // On an abrupt constant-velocity ramp faster than the jump floor per sample, the innovation is
        // the displacement itself, so every sample reads as a jump and the filter runs at the cap until
        // the gaze slows. For a saccade that is exactly right; for fast pursuit it means light
        // filtering with a bounded lag, which is the correct trade at that speed.
        var filter = new OneEuroFilter(minCutoff: 1.5, beta: 60.0);
        long ts = 0;
        int jumps = 0;

        for (int i = 0; i < 100; i++, ts += Period)
        {
            filter.Filter(new GazePoint(100 + (700.0 * PeriodSeconds * i), 540, ts, true));
            if (filter.LastSampleWasJump) jumps++;
        }

        Check.That(jumps).IsEqualTo(99);   // every sample after the initialising one
        Check.That(filter.LastCutoffHz).IsEqualTo(filter.LastCutoffCapHz);
    }

    [Fact]
    public void CutoffCap_IsOneThirdOfTheSampleRate()
    {
        var filter = new OneEuroFilter();
        filter.Filter(new GazePoint(500, 500, 0, true));
        filter.Filter(new GazePoint(500, 500, Period, true));

        Check.That(filter.LastCutoffCapHz).IsCloseTo(1.0 / (3.0 * PeriodSeconds), 1e-9);
    }

    // ── (4) the dt clamp — Alpha() only ──────────────────────────────────────────────────────────

    [Fact]
    public void DtClamp_DoesNotReachTheDerivativeDivisor()
    {
        // A2's sharpest trap. Clamping dt in Alpha() is the fix; clamping it in the SPEED divisor as
        // well would report a legitimate slow catch-up across a dropout as a fast movement, reopen the
        // cutoff, and recreate the very recovery snap the clamp was added to stop.
        //
        // Same displacement, same alpha (both intervals are at or past the 100 ms clamp, so dtSmooth is
        // identical and the filtered points are bit-identical) — only the TRUE elapsed time differs, by
        // exactly 3x. Speed is displacement over time, so the speed-driven part of the cutoff must
        // differ by exactly 3x too. With the divisor clamped, both would read the same and the ratio
        // would collapse to 1.
        const double minCutoff = 1.5;
        double over300Ms = SpeedDrivenCutoffAfterGap(300_000) - minCutoff;
        double over100Ms = SpeedDrivenCutoffAfterGap(100_000) - minCutoff;

        Check.That(over300Ms).IsStrictlyGreaterThan(0.0);          // the adaptive term is live at all
        Check.That(over100Ms / over300Ms).IsCloseTo(3.0, 1e-6);

        static double SpeedDrivenCutoffAfterGap(long gapMicroseconds)
        {
            var filter = new OneEuroFilter(minCutoff, beta: 60.0);
            long ts = 0;
            for (int i = 0; i < 30; i++) { filter.Filter(new GazePoint(500, 500, ts, true)); ts += Period; }
            ts -= Period;   // ts is now the LAST warm-up sample, so the gap below is the exact dt

            // One 10 px move (under the jump floor) spanning the gap, then a sample right after it: the
            // second sample is the one whose derivative divides by the gap.
            ts += gapMicroseconds;
            filter.Filter(new GazePoint(510, 500, ts, true));
            ts += Period;
            filter.Filter(new GazePoint(510, 500, ts, true));

            Check.That(filter.LastSampleWasJump).IsFalse();
            return filter.LastCutoffHz;
        }
    }

    [Fact]
    public void ALargePostDropoutMove_IsBoundedByTheCap_NotByHowLongTheTrackerWasAway()
    {
        // The recovery pop, on the JUMP path. Because the cap and Alpha() are computed from the same
        // dt, a jump sample's alpha is the constant 1/(1 + 3/2pi) ≈ 0.677 whatever dt was — so the
        // bound here belongs to the CAP, and mutation confirms it: widening the dt clamp leaves this
        // test green while removing the cap fails it. Named for what it actually pins.
        var filter = new OneEuroFilter();
        long ts = 0;
        for (int i = 0; i < 30; i++, ts += Period)
            filter.Filter(new GazePoint(500, 500, ts, true));

        var recovered = filter.Filter(new GazePoint(900, 500, ts + 2_000_000, true));

        Check.That(filter.LastSampleWasJump).IsTrue();
        Check.That(recovered.X).IsStrictlyLessThan(900.0);
        Check.That(recovered.X - 500.0).IsCloseTo(0.6769 * 400.0, 1.0);
    }

    [Fact]
    public void ASmallPostDropoutMove_IsStillSmoothed_BecauseAlphaSeesTheClampedDt()
    {
        // The clamp's own job, on the ADAPTIVE path — where dt is not cancelled out and the effect is
        // entirely down to the clamp. A 10 px drift across a 2 s dropout is under the jump threshold,
        // so alpha comes from Alpha(minCutoff, dtSmooth): clamped to 100 ms that is ~0.49, while the
        // true 2 s gives ~0.95 and hands back essentially the raw sample. Both are "filtered" in the
        // loosest sense; only one of them is filtering.
        var filter = new OneEuroFilter();
        long ts = 0;
        for (int i = 0; i < 30; i++, ts += Period)
            filter.Filter(new GazePoint(500, 500, ts, true));

        var recovered = filter.Filter(new GazePoint(510, 500, ts + 2_000_000, true));

        Check.That(filter.LastSampleWasJump).IsFalse();
        Check.That(recovered.X - 500.0).IsCloseTo(0.4852 * 10.0, 0.05);
    }

    // ── Reset ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResetFilter_KEEPSTheNoiseLevelItLearned_BecauseABlinkDoesNotChangeTheTracker()
    {
        // Reverses what an earlier revision of this file asserted, and the reversal is the fix for a
        // real defect, not a preference.
        //
        // GapInterpolator calls Reset() when a dropout outruns the gap budget. Reset means "the position
        // history is void" — it does NOT mean the tracker got quieter. Clearing the noise estimate there
        // handed the first post-blink sample a floor-level (15 px) threshold, and at sigma = 15 px a pure
        // noise sample clears 15 px about 60 % of the time: the filter opened to alpha = 0.677 on noise
        // and emitted a ~13 px pop. That is the post-blink recovery pop this retune exists to calm,
        // re-created by the detector meant to help it.
        var filter = new OneEuroFilter();
        var rng = new Random(7);
        long ts = 0;
        for (int i = 0; i < 400; i++, ts += Period)
            filter.Filter(new GazePoint(960 + (25.0 * Gaussian(rng)), 540 + (25.0 * Gaussian(rng)), ts, true));
        double learned = filter.LastJumpThresholdPixels;
        Check.That(learned).IsStrictlyGreaterThan(40.0);

        filter.Reset();
        filter.Filter(new GazePoint(500, 500, ts, true));               // re-initialises the position
        filter.Filter(new GazePoint(500, 500, ts + Period, true));

        Check.That(filter.LastJumpThresholdPixels).IsCloseTo(learned, 1.0);
    }

    // Both of the next two are ENSEMBLE measurements over 60 noise seeds, and that is not decoration.
    // The defect is probabilistic — at sigma = 15 a pure noise sample clears the 15 px floor about 60 %
    // of the time — so a single-seed version decides nothing. Measured against the shipped 9 px bound, a
    // single seed would WRONGLY PASS on the mutant 23 % of the time and, worse, WRONGLY FAIL on correct
    // code 27 % of the time: a flake in both directions. (An earlier revision of this comment called the
    // pass rate 40 %; that is the per-sample probability of a noise sample failing to clear the floor,
    // which is a different quantity.) An earlier revision of both tests really was single-seed, and
    // mutation showed it: deleting the rebuild's estimate hand-over left the whole suite green.
    //
    // The quantity is the distance the FIRST post-event output travels from the last pre-event one,
    // which is where the pop lands. The two bounds differ because the two innovations do:
    //
    //   rebuild  — the filter keeps running, so the innovation is raw-minus-SMOOTHED: ~21 px RMS at
    //              sigma 15. MEASURED 4.6 px correct, 13.5 px with the estimate cleared. Bound: 6 px.
    //   post-blink — the gap RESETS the filter, so the re-acquisition sample comes back raw and the next
    //              innovation is raw-minus-RAW, i.e. two independent draws: ~30 px RMS, 1.4x larger, so
    //              the correct value is legitimately higher here and the bound moves with it.
    //              MEASURED 6.4 px correct, 17.3 px with the estimate cleared. Bound: 9 px.
    //
    // Both mutation-confirmed: clearing the estimate in Filter's init branch, clearing it in Reset, or
    // dropping the hand-over in GapInterpolator.CopyStateFrom each produce the second number.

    [Fact]
    public void APostBlinkSample_IsNotMistakenForASaccade()
    {
        // A steady sigma = 15 fixation, an over-budget dropout, then the fixation resumes. Driven
        // through GapInterpolator so the real Reset path runs.
        double mean = MeanFirstStepAfter(seed =>
        {
            var interpolator = new GapInterpolator(new OneEuroFilter(), maxGapMs: 150);
            var rng = new Random(seed);
            long ts = 0;
            for (int i = 0; i < 400; i++, ts += Period)
                interpolator.Process(Noisy(960, 540, 15.0, rng, ts));

            ts += 400_000;                                        // 400 ms dropout, past the 150 ms budget
            interpolator.Process(new GazePoint(0, 0, ts, false)); // long gap -> Reset()
            ts += Period;

            // The re-acquisition sample re-initialises the filter and comes back raw; the pop, if any,
            // is on the ones after it.
            var previous = interpolator.Process(Noisy(960, 540, 15.0, rng, ts)).Position;
            var steps = new double[MeasuredSamples];
            for (int k = 0; k < MeasuredSamples; k++)
            {
                ts += Period;
                var next = interpolator.Process(Noisy(960, 540, 15.0, rng, ts)).Position;
                steps[k] = next.DistanceTo(previous);
                previous = next;
            }
            return steps;
        });

        Check.That(mean).IsStrictlyLessThan(9.0);
    }

    [Fact]
    public void AFilterRebuild_DoesNotRestartTheNoiseEstimateFromItsFloor()
    {
        // GazeEngine rebuilds the interpolator on every cursor settings change and on adaptation steps —
        // once per gaze frame while a slider is dragged, up to once a second while the controller ramps.
        // Each rebuild constructs a FRESH filter, so the estimate has to be handed over explicitly.
        double mean = MeanFirstStepAfter(seed =>
        {
            var old = new GapInterpolator(new OneEuroFilter(), maxGapMs: 150);
            var rng = new Random(seed);
            long ts = 0;
            var last = new PhysicalPoint(960, 540);
            for (int i = 0; i < 400; i++, ts += Period)
                last = old.Process(Noisy(960, 540, 15.0, rng, ts)).Position;

            var rebuilt = new GapInterpolator(new OneEuroFilter(), maxGapMs: 150);
            rebuilt.CopyStateFrom(old);
            rebuilt.WarmStartFromCurrentState();

            // Measured from where the old filter left the cursor, across the first few samples through
            // the new one — see MeasuredSamples for why not just the first.
            var steps = new double[MeasuredSamples];
            for (int k = 0; k < MeasuredSamples; k++)
            {
                var next = rebuilt.Process(Noisy(960, 540, 15.0, rng, ts)).Position;
                steps[k] = next.DistanceTo(last);
                last = next;
                ts += Period;
            }
            return steps;
        });

        Check.That(mean).IsStrictlyLessThan(6.0);
    }

    /// <summary>
    /// Mean per-sample displacement over the first <see cref="MeasuredSamples"/> samples after the event,
    /// across 60 noise seeds.
    /// </summary>
    /// <remarks>
    /// THREE samples, not one, and that is a review fix — but it is the smaller half of it. A mutant that
    /// carried the RMS but not the sample COUNT survived the one-sample version, and it survived the
    /// three-sample version too: measured 3-sample means at sigma = 15 are <b>5.31 px correct, 5.99 px
    /// carrying the RMS alone, 12.90 px carrying nothing</b>. Partial adoption is a real degradation, but
    /// a small one — it sat 0.007 px inside this test's 6 px bound, and only a bound tuned far tighter
    /// than the measurement noise justifies would have caught it.
    /// <para>
    /// So the estimate became a single <c>NoiseEstimate</c> field rather than two loose ones, which makes
    /// a partial hand-over unexpressible. This averaging stays because measuring three samples is simply
    /// a better measurement than measuring one — not because it is what closes that hole.
    /// </para>
    /// </remarks>
    private const int MeasuredSamples = 3;

    private static double MeanFirstStepAfter(Func<int, double[]> trial)
    {
        double total = 0;
        const int seeds = 60;
        for (int seed = 1; seed <= seeds; seed++)
        {
            double[] steps = trial(seed);
            Check.That(steps).HasSize(MeasuredSamples);
            total += steps.Sum();
        }
        return total / (seeds * MeasuredSamples);
    }

    [Fact]
    public void AModeSwitchIntoOneEuro_StartsWithAnEmptyEstimate_AndSaysSo()
    {
        // The negative case, so "carry the estimate" cannot quietly become "carry something arbitrary".
        // An EMA filter has no innovation estimate to hand over, so the incoming One-Euro correctly
        // starts empty and runs on its floor until it has learned something.
        var old = new GapInterpolator(new SmoothedFilter(0.5), maxGapMs: 150);
        long ts = 0;
        for (int i = 0; i < 50; i++, ts += Period)
            old.Process(new GazePoint(960, 540, ts, true));

        var filter = new OneEuroFilter();
        var rebuilt = new GapInterpolator(filter, maxGapMs: 150);
        rebuilt.CopyStateFrom(old);
        rebuilt.WarmStartFromCurrentState();
        rebuilt.Process(new GazePoint(960, 540, ts, true));

        Check.That(filter.LastJumpThresholdPixels).IsEqualTo(15.0);
    }

    private static GazePoint Noisy(double x, double y, double sigma, Random rng, long ts) =>
        new(x + (sigma * Gaussian(rng)), y + (sigma * Gaussian(rng)), ts, IsValid: true);

    /// <summary>Standard-normal sample via Box–Muller — mirrors <c>GazeStreamGenerator</c>'s.</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
