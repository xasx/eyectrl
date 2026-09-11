namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The metric families the debate pre-registered, computed from a <see cref="ReplayPipeline"/> run.
/// <para>
/// This exists because the product's only jitter number measures the wrong thing:
/// <c>LastFixationJitter</c> is post-filter, speed-domain, latched at fixation end, deflated by gap
/// samples, and off by default. Every metric here is instead computed offline from the raw stream and
/// a named candidate pipeline, so two candidates can be compared on identical input.
/// </para>
/// <para>
/// All names carry the <c>Replay</c> prefix where the number cannot include what the UI thread does
/// to it — see <see cref="ReplayPipeline"/>.
/// </para>
/// </summary>
public static class GazeMetrics
{
    // ── Stream shape ──────────────────────────────────────────────────────────

    /// <summary>What the stream itself looks like, independent of any pipeline: rate, dropouts, gaps.</summary>
    /// <param name="SampleCount">Total samples in the recording.</param>
    /// <param name="InvalidFraction">Fraction of samples the tracker marked invalid (eyes lost).</param>
    /// <param name="MedianDeltaMicroseconds">Median inter-sample interval — the real sample rate, not the nominal one.</param>
    /// <param name="DeltaHistogram">Inter-sample intervals bucketed by millisecond, ascending by bucket.</param>
    /// <param name="ShortGapCount">Invalid runs the pipeline holds through (within the gap budget).</param>
    /// <param name="LongGapCount">Invalid runs that exceed the budget and break the sequence.</param>
    public sealed record StreamShape(
        int SampleCount,
        double InvalidFraction,
        long MedianDeltaMicroseconds,
        IReadOnlyList<(int Milliseconds, int Count)> DeltaHistogram,
        int ShortGapCount,
        int LongGapCount);

    /// <summary>Measures the raw stream's shape. <paramref name="maxGapMs"/> is the pipeline's gap budget.</summary>
    public static StreamShape MeasureStream(IReadOnlyList<GazePoint> samples, int maxGapMs = 150)
    {
        if (samples.Count == 0)
            return new StreamShape(0, 0, 0, Array.Empty<(int, int)>(), 0, 0);

        int invalid = 0;
        var deltas = new List<long>(Math.Max(0, samples.Count - 1));
        var histogram = new SortedDictionary<int, int>();

        for (int i = 0; i < samples.Count; i++)
        {
            if (!samples[i].IsValid) invalid++;
            if (i == 0) continue;

            long d = samples[i].TimestampMicroseconds - samples[i - 1].TimestampMicroseconds;
            deltas.Add(d);
            int ms = (int)Math.Round(d / 1000.0);
            histogram[ms] = histogram.TryGetValue(ms, out int c) ? c + 1 : 1;
        }

        // Invalid RUNS, not invalid samples: one blink is one gap however many samples it spans, and
        // the short/long split is what decides whether the pipeline holds or resets.
        //
        // A run at either END of the stream counts too. The leading one especially: a recording that
        // starts with a dropout is the tracker not having acquired the eyes yet, which is the most
        // interesting dropout there is for acquisition-time analysis — silently dropping it would
        // report zero gaps for a session that began with the longest one.
        int shortGaps = 0, longGaps = 0;
        long budgetUs = maxGapMs * MathConstants.MicrosecondsPerMillisecond;
        int run = 0;
        long lastValidTs = long.MinValue;
        for (int i = 0; i < samples.Count; i++)
        {
            if (!samples[i].IsValid) { run++; continue; }
            if (run > 0)
            {
                // Before the first valid sample there is no previous one to span from, so the run is
                // measured from the start of the stream — an exact duration, not an estimate.
                long from = lastValidTs != long.MinValue ? lastValidTs : samples[0].TimestampMicroseconds;
                long span = samples[i].TimestampMicroseconds - from;
                if (span >= budgetUs) longGaps++; else shortGaps++;
            }
            run = 0;
            lastValidTs = samples[i].TimestampMicroseconds;
        }
        if (run > 0)   // stream ended mid-gap (or never contained a valid sample at all)
        {
            long from = lastValidTs != long.MinValue ? lastValidTs : samples[0].TimestampMicroseconds;
            long span = samples[^1].TimestampMicroseconds - from;
            if (span >= budgetUs) longGaps++; else shortGaps++;
        }

        deltas.Sort();
        long median = deltas.Count == 0 ? 0 : deltas[deltas.Count / 2];
        return new StreamShape(
            samples.Count,
            (double)invalid / samples.Count,
            median,
            histogram.Select(kv => (kv.Key, kv.Value)).ToList(),
            shortGaps,
            longGaps);
    }

    // ── Fixation stability ────────────────────────────────────────────────────

    /// <summary>Radial deviation of the filtered point during fixation — the headline stability number.</summary>
    /// <param name="P95Pixels">95th-percentile radial distance from the reference point.</param>
    /// <param name="RmsPixels">Root-mean-square radial distance — the whole distribution, not just its tail.</param>
    /// <param name="SampleCount">Samples that contributed (gap-held samples are excluded).</param>
    public sealed record FixationDeviation(double P95Pixels, double RmsPixels, int SampleCount);

    /// <summary>
    /// Radial deviation over <paramref name="segment"/>, measured against <paramref name="groundTruth"/>
    /// when the session was scripted, and against the segment's own centroid otherwise.
    /// <para>
    /// Gap-held samples are excluded: their position is frozen, so counting them would credit a
    /// pipeline for perfect stability during a blink — the exact way a stability metric flatters
    /// itself when tracking is worst.
    /// </para>
    /// </summary>
    public static FixationDeviation MeasureFixationDeviation(
        IReadOnlyList<ReplayPipeline.Frame> segment, PhysicalPoint? groundTruth = null)
    {
        var points = segment.Where(f => !f.IsInGap).Select(f => f.Filtered).ToList();
        if (points.Count == 0) return new FixationDeviation(0, 0, 0);

        var reference = groundTruth ?? new PhysicalPoint(points.Average(p => p.X), points.Average(p => p.Y));

        var radii = points.Select(p => p.DistanceTo(reference)).OrderBy(r => r).ToArray();
        double rms = Math.Sqrt(radii.Sum(r => r * r) / radii.Length);
        return new FixationDeviation(Percentile(radii, 0.95), rms, radii.Length);
    }

    /// <summary>
    /// Splits a replay into fixation segments by dispersion: a segment continues while every sample
    /// stays within <paramref name="radiusPixels"/> of the running mean. Used for free-view recordings,
    /// where there is no scripted target to measure against.
    /// </summary>
    public static List<List<ReplayPipeline.Frame>> SegmentFixations(
        IReadOnlyList<ReplayPipeline.Frame> frames, double radiusPixels = 50, int minSamples = 8)
    {
        var segments = new List<List<ReplayPipeline.Frame>>();
        var current = new List<ReplayPipeline.Frame>();
        double sumX = 0, sumY = 0;

        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;   // a held point is not evidence of fixating

            if (current.Count == 0)
            {
                current.Add(frame);
                sumX = frame.Filtered.X; sumY = frame.Filtered.Y;
                continue;
            }

            var mean = new PhysicalPoint(sumX / current.Count, sumY / current.Count);
            if (frame.Filtered.DistanceTo(mean) <= radiusPixels)
            {
                current.Add(frame);
                sumX += frame.Filtered.X; sumY += frame.Filtered.Y;
            }
            else
            {
                if (current.Count >= minSamples) segments.Add(current);
                current = new List<ReplayPipeline.Frame> { frame };
                sumX = frame.Filtered.X; sumY = frame.Filtered.Y;
            }
        }
        if (current.Count >= minSamples) segments.Add(current);
        return segments;
    }

    // ── Pops ──────────────────────────────────────────────────────────────────

    /// <summary>How long a pop lasted. The buckets exist because a filter and a gap fix move different ones.</summary>
    public enum PopDuration
    {
        /// <summary>A single sample — the impulsive outlier a median removes and a low-pass only smears.</summary>
        Isolated,
        /// <summary>Two to four samples.</summary>
        Short,
        /// <summary>Five or more — no longer an outlier; the signal genuinely went somewhere.</summary>
        Sustained,
    }

    /// <summary>What produced a pop. Cross-tabbing prevents two fixes claiming the same improvement.</summary>
    public enum PopCause
    {
        /// <summary>The pop touches a dropout — a gap fix owns it.</summary>
        Gap,
        /// <summary>No dropout nearby: the tracker reported this excursion as real data.</summary>
        Signal,
    }

    /// <summary>One detected excursion from the local baseline.</summary>
    /// <param name="StartIndex">Index of the first sample of the pop.</param>
    /// <param name="Length">Length in samples.</param>
    /// <param name="PeakPixels">Largest radial excursion within the pop.</param>
    /// <param name="Duration">Duration bucket.</param>
    /// <param name="Cause">Gap or signal.</param>
    public sealed record Pop(int StartIndex, int Length, double PeakPixels, PopDuration Duration, PopCause Cause);

    /// <summary>
    /// Counts excursions of the filtered point beyond <paramref name="thresholdPixels"/> from a trailing
    /// median baseline, bucketed by duration and cross-tabbed with cause.
    /// <para>
    /// The cross-tab is the point. A median prefilter and the gap fixes both reduce "pops"; without
    /// separating dropout-adjacent excursions from signal ones, each could be credited with the other's
    /// improvement and the acceptance gate would pass on double-counted evidence.
    /// </para>
    /// </summary>
    public static List<Pop> DetectPops(
        IReadOnlyList<ReplayPipeline.Frame> frames, double thresholdPixels = 40, int baselineWindow = 9)
    {
        var pops = new List<Pop>();
        if (frames.Count < baselineWindow + 1) return pops;

        int i = baselineWindow;
        while (i < frames.Count)
        {
            var baseline = TrailingMedian(frames, i, baselineWindow);
            double distance = frames[i].Filtered.DistanceTo(baseline);
            if (distance <= thresholdPixels) { i++; continue; }

            int start = i;
            double peak = distance;
            while (i < frames.Count && frames[i].Filtered.DistanceTo(baseline) > thresholdPixels)
            {
                peak = Math.Max(peak, frames[i].Filtered.DistanceTo(baseline));
                i++;
            }

            int length = i - start;
            pops.Add(new Pop(
                start, length, peak,
                length == 1 ? PopDuration.Isolated : length <= 4 ? PopDuration.Short : PopDuration.Sustained,
                TouchesGap(frames, start, length) ? PopCause.Gap : PopCause.Signal));
        }
        return pops;
    }

    // A pop is attributed to the gap lane if a dropout occurs anywhere within it or in the sample
    // immediately before it — recovery pops land on the first sample AFTER the gap ends.
    private static bool TouchesGap(IReadOnlyList<ReplayPipeline.Frame> frames, int start, int length)
    {
        int from = Math.Max(0, start - 1);
        for (int i = from; i < start + length && i < frames.Count; i++)
            if (frames[i].IsInGap || !frames[i].Raw.IsValid) return true;
        return false;
    }

    private static PhysicalPoint TrailingMedian(IReadOnlyList<ReplayPipeline.Frame> frames, int index, int window)
    {
        int from = Math.Max(0, index - window);
        var xs = new List<double>(window);
        var ys = new List<double>(window);
        for (int i = from; i < index; i++)
        {
            xs.Add(frames[i].Filtered.X);
            ys.Add(frames[i].Filtered.Y);
        }
        xs.Sort(); ys.Sort();
        return new PhysicalPoint(xs[xs.Count / 2], ys[ys.Count / 2]);
    }

    // ── Responsiveness ────────────────────────────────────────────────────────

    /// <summary>
    /// Milliseconds from a step in the raw input until the filtered output first covers 90 % of it and
    /// stays there. This is the number a smoothing change is allowed to spend, and the debate's gate
    /// rejects on it regardless of how good the stability number looks.
    /// </summary>
    /// <returns>Latency in milliseconds, or null when the output never settles within the stream.</returns>
    public static double? MeasureStepLatencyMs(IReadOnlyList<ReplayPipeline.Frame> frames, int stepIndex)
    {
        if (stepIndex <= 0 || stepIndex >= frames.Count) return null;

        var before = frames[stepIndex - 1].Filtered;
        var target = new PhysicalPoint(frames[^1].Raw.X, frames[^1].Raw.Y);
        double stepSize = target.DistanceTo(before);
        if (stepSize <= 0) return null;

        long stepTs = frames[stepIndex].Raw.TimestampMicroseconds;
        for (int i = stepIndex; i < frames.Count; i++)
        {
            double covered = frames[i].Filtered.DistanceTo(before);
            if (covered < 0.9 * stepSize) continue;

            // Must STAY inside the band: a filter that overshoots through 90 % and comes back has not
            // settled, and crediting the crossing would reward ringing.
            bool settled = true;
            for (int j = i; j < frames.Count; j++)
            {
                if (frames[j].Filtered.DistanceTo(before) < 0.9 * stepSize) { settled = false; break; }
            }
            if (!settled) continue;

            return (frames[i].Raw.TimestampMicroseconds - stepTs) / 1000.0;
        }
        return null;
    }

    // ── Dwell ─────────────────────────────────────────────────────────────────

    /// <summary>Dwell behaviour over a replayed single target. Replay-only: there is no UI dispatcher here.</summary>
    /// <param name="TimeToActivateMs">Milliseconds from the first sample until the target activated, or null if it never did.</param>
    /// <param name="ProgressResets">Times accumulated progress fell back to zero without activating.</param>
    /// <param name="ResetsPerMinute">The same, normalised by stream duration — the debate's gate metric.</param>
    /// <param name="Activations">Total activations over the stream.</param>
    public sealed record ReplayDwell(double? TimeToActivateMs, int ProgressResets, double ResetsPerMinute, int Activations);

    /// <summary>
    /// Replays the frames through a real <see cref="DwellDetector"/> holding one circular target — the
    /// debate's 80 px button — and reports time-to-activate and progress resets per minute.
    /// <para>
    /// Gap-held frames are skipped, matching what the shipped dwell surfaces now do, so this measures
    /// the same behaviour the user gets rather than an idealised one.
    /// </para>
    /// </summary>
    public static ReplayDwell MeasureDwell(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        PhysicalPoint targetCenter,
        double targetRadiusPixels = 40,
        int dwellMs = 600)
    {
        var detector = new DwellDetector(cooldownMs: 0);
        detector.AddTarget(new DwellTarget("replay-target", targetCenter, targetRadiusPixels, dwellMs));

        int resets = 0, activations = 0;
        double lastProgress = 0;
        long? firstTs = null, activatedTs = null;

        detector.TargetProgress += (_, progress) =>
        {
            // A fall to zero that is not an activation is the dwell the user lost — the reset the gate
            // counts. Activation also reports zero, so it is excluded by checking the activation flag.
            if (progress == 0 && lastProgress > 0) resets++;
            lastProgress = progress;
        };
        detector.TargetActivated += _ => { activations++; lastProgress = 0; };

        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;
            firstTs ??= frame.Raw.TimestampMicroseconds;

            int before = activations;
            detector.Update(frame.Filtered, frame.Raw.TimestampMicroseconds);
            if (activations > before && activatedTs is null)
                activatedTs = frame.Raw.TimestampMicroseconds;
        }

        // Activation itself emits progress 0, which the handler above would otherwise count as a reset.
        resets = Math.Max(0, resets - activations);

        double? timeToActivate = activatedTs is { } a && firstTs is { } f ? (a - f) / 1000.0 : null;
        double minutes = frames.Count < 2
            ? 0
            : (frames[^1].Raw.TimestampMicroseconds - frames[0].Raw.TimestampMicroseconds) / 60_000_000.0;
        return new ReplayDwell(timeToActivate, resets, minutes > 0 ? resets / minutes : 0, activations);
    }

    // ── Spectrum ──────────────────────────────────────────────────────────────

    /// <summary>One power-spectral-density bin.</summary>
    /// <param name="Hz">Bin centre frequency.</param>
    /// <param name="Power">Mean power of the X and Y components at that frequency.</param>
    public readonly record struct PsdBin(double Hz, double Power);

    /// <summary>
    /// Power spectral density of a fixation segment, mean-removed, by direct DFT.
    /// <para>
    /// This settles the debate's pre-registered falsifier: if fixation energy is dominated below 2 Hz
    /// it is drift rather than noise, a low-pass cannot help, and the whole filter stage is withdrawn.
    /// A direct transform rather than an FFT because a fixation segment is a few hundred samples and
    /// clarity beats speed in a tool whose job is to be trusted.
    /// </para>
    /// </summary>
    /// <param name="segment">Frames of one fixation segment; gap-held frames are dropped.</param>
    /// <param name="sampleRateHz">
    /// Assumed UNIFORM sample rate — this transform treats sample index as time and does not read the
    /// recorded timestamps. A real recording is not uniformly sampled (dropouts, jitter in the device's
    /// delivery), so pass the measured rate rather than the device's nominal one:
    /// <c>1e6 / MeasureStream(samples).MedianDeltaMicroseconds</c>. Passing a nominal rate that the
    /// session did not actually achieve shifts every bin, which matters because the below-2 Hz
    /// falsifier is decided on where the power sits.
    /// </param>
    /// <param name="bins">Number of bins between 0 and Nyquist.</param>
    public static List<PsdBin> ComputePsd(IReadOnlyList<ReplayPipeline.Frame> segment, double sampleRateHz, int bins = 64)
    {
        var xs = segment.Where(f => !f.IsInGap).Select(f => f.Filtered.X).ToArray();
        var ys = segment.Where(f => !f.IsInGap).Select(f => f.Filtered.Y).ToArray();
        var result = new List<PsdBin>(bins);
        if (xs.Length < 4) return result;

        double meanX = xs.Average(), meanY = ys.Average();
        int n = xs.Length;
        double nyquist = sampleRateHz / 2.0;

        for (int k = 1; k <= bins; k++)
        {
            double hz = nyquist * k / bins;
            double reX = 0, imX = 0, reY = 0, imY = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * hz * i / sampleRateHz;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                reX += (xs[i] - meanX) * cos; imX -= (xs[i] - meanX) * sin;
                reY += (ys[i] - meanY) * cos; imY -= (ys[i] - meanY) * sin;
            }
            double powerX = (reX * reX + imX * imX) / (n * n);
            double powerY = (reY * reY + imY * imY) / (n * n);
            result.Add(new PsdBin(hz, (powerX + powerY) / 2.0));
        }
        return result;
    }

    /// <summary>Fraction of total spectral power below <paramref name="hz"/> — the falsifier's test statistic.</summary>
    public static double PowerFractionBelow(IReadOnlyList<PsdBin> psd, double hz)
    {
        double total = psd.Sum(b => b.Power);
        return total <= 0 ? 0 : psd.Where(b => b.Hz < hz).Sum(b => b.Power) / total;
    }

    private static double Percentile(double[] sortedAscending, double fraction)
    {
        if (sortedAscending.Length == 0) return 0;
        if (sortedAscending.Length == 1) return sortedAscending[0];
        double position = fraction * (sortedAscending.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sortedAscending.Length - 1);
        return sortedAscending[lower] + (position - lower) * (sortedAscending[upper] - sortedAscending[lower]);
    }
}
