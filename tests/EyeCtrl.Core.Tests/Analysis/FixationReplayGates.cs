namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// The fixation debate's acceptance gates, executable on any stream: fixation runs from the real
/// <see cref="FixationDetector"/>, held episodes from the pinned I-DT definition, and the G1-G6
/// metrics computed from the two.
/// <para>
/// This is the ONE committed instrument the later tickets measure against. The debate's numbers came
/// from throwaway harnesses in agent scratchpads that disagreed by 10-15 points on the centroid rule;
/// a decision about the rule (ticket 3, R75 vs R60) needs a reproducible instrument, not two of them.
/// Everything here is therefore pinned: the detector is the shipped class, the filter is the shipped
/// default, and every threshold below is a debate constant with its source cited.
/// </para>
/// <para>
/// What the replay cannot see: suppression, state, the dispatcher. The live engine feeds the detector
/// only from FreeLook with <c>SuppressFixation</c> false, so the replay is the steady state the gates
/// are about. Numbers derived here are labelled as replay numbers throughout.
/// </para>
/// </summary>
public static class FixationReplayGates
{
    // ── The pinned definition ─────────────────────────────────────────────────
    // Every constant below comes from the final synthesis or the designer's cross-review; none is a
    // tuning knob. A change to one is a change to the agreed measurement, so it belongs in the ticket
    // that argues for it, not in this file.

    /// <summary>Fixation-detection time (<c>DwellSettings</c> default). The activation clock starts only when a fixation is reported.</summary>
    public const int FixationDetectionMs = 300;

    /// <summary>Activation-button dwell (<c>DwellSettings</c> default). A run is "activatable" when it survives this long after its onset event.</summary>
    public const int ActivationButtonDwellMs = 700;

    /// <summary>Keyboard-key dwell (<c>DwellSettings</c> default) — the M2 "keyboard-length hold" threshold.</summary>
    public const int KeyboardKeyDwellMs = 450;

    /// <summary>
    /// The served@place bound in physical pixels: how far the activation point may sit from the held
    /// episode's median and still count as serving it. 100 px is the debate's shared constant.
    /// </summary>
    public const double ServedDistancePx = 100;

    /// <summary>
    /// How late after a held episode's start its serving fixation may still have started. One second of
    /// episode plus 300 ms of detection slack; the debate's "within 1.3 s".
    /// </summary>
    public const int ServedOnsetWindowMs = 1300;

    /// <summary>Minimum duration of an I-DT held episode — the debate's "held means ≥ 1 s".</summary>
    public const int EpisodeMinDurationMs = 1000;

    /// <summary>I-DT dispersion at D150, measured as the bounding box's diagonal in physical pixels.</summary>
    public const double Dispersion150Px = 150;

    /// <summary>I-DT dispersion at D200, measured as the bounding box's diagonal in physical pixels.</summary>
    public const double Dispersion200Px = 200;

    /// <summary>
    /// A gap longer than this breaks an I-DT episode. Shorter dropouts are skipped, not evidence of
    /// leaving, matching the 500 ms pause rule the debate adopted for fixation age. On the 2026-09-12
    /// recording no gap reaches this, so it changes nothing there; it exists so a lost-eyes stretch can
    /// never be stitched into one "hold".
    /// </summary>
    public const int EpisodeGapBreakMs = 500;

    /// <summary>The pipeline's gap budget, pinned to the shipped default (<c>CursorSettings</c>).</summary>
    public const int MaxGapInterpolationMs = 150;

    // Gate bars. G1 is primary; G2 accuracy; G3 is reported only; G4-G6 are guards.

    /// <summary>G1 bar at D150 — the primary gate.</summary>
    public const double G1BarD150 = 0.75;

    /// <summary>G1 bar at D200.</summary>
    public const double G1BarD200 = 0.60;

    /// <summary>G2 bar: at most this share of episodes may be served only by a misplaced fixation.</summary>
    public const double G2Bar = 0.05;

    /// <summary>G4 cap: flicker may not exceed this multiple of today's rule at the same radius.</summary>
    public const double G4FlickerCap = 1.25;

    /// <summary>G6 bar: time from stream start to the first reported fixation.</summary>
    public const double G6BarSeconds = 2.0;

    /// <summary>G3 is a reported target, not a gate: at most about one idle activatable fixation per minute.</summary>
    public const double G3TargetPerMinute = 1.0;

    // ── Inputs and measured rows ──────────────────────────────────────────────

    /// <summary>
    /// One candidate rule: a fixation radius, a filter factory and a gap budget. The detector is always
    /// the real <see cref="FixationDetector"/> — only its radius varies between candidates.
    /// </summary>
    /// <param name="Name">Label used in the report.</param>
    /// <param name="RadiusPixels">Fixed radius of the shipped first-sample-anchor detector, in physical pixels.</param>
    /// <param name="CreateFilter">Factory for a FRESH filter; replay must never reuse filter state across runs.</param>
    /// <param name="MaxGapMs">The <c>MaxGapInterpolationMs</c> this candidate runs with.</param>
    public sealed record Candidate(
        string Name,
        double RadiusPixels,
        Func<IGazeFilter> CreateFilter,
        int MaxGapMs = MaxGapInterpolationMs);

    /// <summary>
    /// Today's shipped detector at <paramref name="radiusPixels"/>: the first-sample anchor with the
    /// retuned One-Euro filter (<c>OneEuroFilter</c>'s defaults) and the shipped gap budget.
    /// </summary>
    public static Candidate TodayAtRadius(double radiusPixels) =>
        new($"today's rule, r{radiusPixels:0}", radiusPixels, () => new OneEuroFilter());

    /// <summary>Today's rule at r50 — the radius the user's eye sessions actually ran (skeptic's log reconstruction).</summary>
    public static Candidate TodayR50 { get; } = TodayAtRadius(50);

    /// <summary>Today's rule at r90 — the value in <c>settings.json</c> at the last checkpoint.</summary>
    public static Candidate TodayR90 { get; } = TodayAtRadius(90);

    /// <summary>One fixation run, exactly as the real detector reported it.</summary>
    /// <param name="Anchor">The activation point today: the filtered point of the anchor sample, reported by <c>FixationStarted</c>.</param>
    /// <param name="OnsetUs">Timestamp of the sample on which <c>FixationStarted</c> fired — when the button would appear.</param>
    /// <param name="EndUs">Timestamp of the sample on which <c>FixationEnded</c> fired, or the last fed sample when the stream ended first.</param>
    /// <param name="EndedByLeave">False when the stream ended while the run was still open; such a run never produced a Fixation→FreeLook transition.</param>
    public sealed record FixationRun(PhysicalPoint Anchor, long OnsetUs, long EndUs, bool EndedByLeave)
    {
        /// <summary>How long the dwell window stayed open after the onset — the button's usable hold, in milliseconds.</summary>
        public double HoldAfterOnsetMs => (EndUs - OnsetUs) / 1000.0;

        /// <summary>True when the run survives <see cref="ActivationButtonDwellMs"/> past its onset — the M1 "activatable fixation".</summary>
        public bool IsActivatable => HoldAfterOnsetMs >= ActivationButtonDwellMs;

        /// <summary>True when the run survives <see cref="KeyboardKeyDwellMs"/> past its onset — the M2 "keyboard-length hold".</summary>
        public bool IsKeyboardLength => HoldAfterOnsetMs >= KeyboardKeyDwellMs;
    }

    /// <summary>A held episode under the pinned I-DT definition: one low-dispersion run lasting at least one second.</summary>
    /// <param name="StartUs">Timestamp of the episode's first sample.</param>
    /// <param name="EndUs">Timestamp of the episode's last sample.</param>
    /// <param name="Median">Component-wise median of the episode's filtered points; the place the episode is "at".</param>
    /// <param name="SampleCount">Samples that contributed (gap-held samples never do).</param>
    /// <param name="BoxDiagonalPx">The episode's bounding-box diagonal — the dispersion it was held within.</param>
    public sealed record HeldEpisode(long StartUs, long EndUs, PhysicalPoint Median, int SampleCount, double BoxDiagonalPx)
    {
        /// <summary>Episode duration in seconds; the I-DT definition requires at least one.</summary>
        public double DurationSeconds => (EndUs - StartUs) / 1_000_000.0;
    }

    /// <summary>The gate numbers at one dispersion threshold for one candidate.</summary>
    /// <param name="DispersionPx">The I-DT dispersion (D150 or D200).</param>
    /// <param name="EpisodeCount">Held episodes at this dispersion.</param>
    /// <param name="ServedEpisodes">Episodes with an activatable fixation in the onset window at ≤ <see cref="ServedDistancePx"/> from the median.</param>
    /// <param name="ElsewhereEpisodes">Episodes whose only in-window activatable fixations are misplaced.</param>
    /// <param name="MidasRuns">Activatable fixations that start in no held episode — the idle-trigger proxy.</param>
    public sealed record GateRow(
        double DispersionPx,
        int EpisodeCount,
        int ServedEpisodes,
        int ElsewhereEpisodes,
        int MidasRuns,
        double Minutes)
    {
        /// <summary>G1: the share of held episodes that are served at place.</summary>
        public double ServedShare => EpisodeCount == 0 ? 0 : (double)ServedEpisodes / EpisodeCount;

        /// <summary>G2: the share of held episodes served only by a misplaced fixation.</summary>
        public double ElsewhereShare => EpisodeCount == 0 ? 0 : (double)ElsewhereEpisodes / EpisodeCount;

        /// <summary>G3: idle activatable fixations per minute (reported only).</summary>
        public double MidasPerMinute => Minutes <= 0 ? 0 : MidasRuns / Minutes;

        /// <summary>The G1 bar this dispersion is judged against.</summary>
        public double ServedBar => DispersionPx <= Dispersion150Px ? G1BarD150 : G1BarD200;

        /// <summary>Whether G1 passes at this dispersion.</summary>
        public bool ServedPasses => EpisodeCount > 0 && ServedShare >= ServedBar;

        /// <summary>Whether G2 passes at this dispersion.</summary>
        public bool ElsewherePasses => ElsewhereShare <= G2Bar;
    }

    /// <summary>Everything one candidate produced on one stream.</summary>
    /// <param name="Candidate">The candidate label and rule.</param>
    /// <param name="SampleCount">Samples in the stream.</param>
    /// <param name="DurationSeconds">Stream duration from first to last timestamp.</param>
    /// <param name="InvalidFraction">Share of samples the tracker marked invalid, for context.</param>
    /// <param name="Runs">Every fixation run the real detector reported.</param>
    /// <param name="ActivatableRuns">Runs surviving <see cref="ActivationButtonDwellMs"/> past onset (M1).</param>
    /// <param name="KeyboardRuns">Runs surviving <see cref="KeyboardKeyDwellMs"/> past onset (M2).</param>
    /// <param name="EndedRuns">Runs that produced a Fixation→FreeLook transition (M4).</param>
    /// <param name="TimeToFirstFixationSeconds">Onset of the first run minus the stream's first timestamp (M3), or null when no run formed.</param>
    /// <param name="At150">Gate numbers at D150.</param>
    /// <param name="At200">Gate numbers at D200.</param>
    public sealed record FixationAnalysis(
        Candidate Candidate,
        int SampleCount,
        double DurationSeconds,
        double InvalidFraction,
        IReadOnlyList<FixationRun> Runs,
        int ActivatableRuns,
        int KeyboardRuns,
        int EndedRuns,
        double? TimeToFirstFixationSeconds,
        GateRow At150,
        GateRow At200)
    {
        /// <summary>Stream duration in minutes; the denominator of every per-minute metric.</summary>
        public double Minutes => DurationSeconds / 60.0;

        /// <summary>M1 (informational): activatable fixations per minute.</summary>
        public double ActivatablePerMinute => Minutes <= 0 ? 0 : ActivatableRuns / Minutes;

        /// <summary>M2 (G5): keyboard-length holds per minute.</summary>
        public double KeyboardPerMinute => Minutes <= 0 ? 0 : KeyboardRuns / Minutes;

        /// <summary>M4 (G4): Fixation→FreeLook transitions per minute.</summary>
        public double FlickerPerMinute => Minutes <= 0 ? 0 : EndedRuns / Minutes;

        /// <summary>G6: whether the first fixation was reported within the bar.</summary>
        public bool TimeToFirstFixationPasses =>
            TimeToFirstFixationSeconds is { } ttf && ttf <= G6BarSeconds;

        /// <summary>Today's rule at r90 must fail G1 on the debate's recording — the "new gates fail on old code" guard.</summary>
        public bool FailsPrimaryGate => !At150.ServedPasses || !At200.ServedPasses;
    }

    // ── Replay ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replays a raw stream through the real gap interpolator + filter, then through the real
    /// <see cref="FixationDetector"/> at the candidate's radius, and computes the gate numbers.
    /// Deterministic: no clock, no device, no UI.
    /// </summary>
    /// <param name="samples">Raw samples, exactly as recorded.</param>
    /// <param name="candidate">The candidate rule under test.</param>
    /// <param name="calibration">The session's calibration transform, or null for an uncalibrated session.</param>
    /// <param name="servedDistancePx">
    /// The served@place bound. Defaults to the pinned <see cref="ServedDistancePx"/>; the seam exists so
    /// a test can move the bound and prove the metric depends on it. Callers measuring the gate omit it.
    /// </param>
    public static FixationAnalysis Analyze(
        IReadOnlyList<GazePoint> samples,
        Candidate candidate,
        AffineTransformParameters? calibration = null,
        double servedDistancePx = ServedDistancePx)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(candidate);
        if (samples.Count == 0)
            throw new ArgumentException("The stream contains no samples.", nameof(samples));

        var pipelineCandidate = new ReplayPipeline.Candidate(
            candidate.Name, candidate.CreateFilter, candidate.MaxGapMs);
        var frames = ReplayPipeline.RunSynthetic(samples, pipelineCandidate, calibration);

        double durationSeconds = (samples[^1].TimestampMicroseconds - samples[0].TimestampMicroseconds) / 1_000_000.0;
        double minutes = durationSeconds / 60.0;
        int invalid = samples.Count(s => !s.IsValid);

        var runs = DetectRuns(frames, candidate.RadiusPixels);
        var activatable = runs.Where(r => r.IsActivatable).ToList();

        var episodes150 = ExtractHeldEpisodes(frames, Dispersion150Px);
        var episodes200 = ExtractHeldEpisodes(frames, Dispersion200Px);

        double? timeToFirstFixation = runs.Count > 0
            ? (runs[0].OnsetUs - samples[0].TimestampMicroseconds) / 1_000_000.0
            : null;

        return new FixationAnalysis(
            candidate,
            samples.Count,
            durationSeconds,
            (double)invalid / samples.Count,
            runs,
            activatable.Count,
            runs.Count(r => r.IsKeyboardLength),
            runs.Count(r => r.EndedByLeave),
            timeToFirstFixation,
            MeasureAt(episodes150, activatable, minutes, Dispersion150Px, servedDistancePx),
            MeasureAt(episodes200, activatable, minutes, Dispersion200Px, servedDistancePx));
    }

    /// <summary>
    /// Feeds the non-gap filtered samples through a fresh real <see cref="FixationDetector"/> and
    /// captures its events as runs. Mirrors <c>GazeEngine.ProcessGazePoint</c>: gap-held samples are
    /// skipped (the live engine's <c>!isInGap</c> guard), and nothing suppresses detection.
    /// </summary>
    public static List<FixationRun> DetectRuns(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        double radiusPixels,
        int fixationMs = FixationDetectionMs)
    {
        var detector = new FixationDetector(radiusPixels, fixationMs);
        var runs = new List<FixationRun>();

        long currentUs = 0;
        PhysicalPoint anchor = default;
        long onsetUs = 0;
        bool open = false;

        detector.FixationStarted += point =>
        {
            anchor = point;
            onsetUs = currentUs;
            open = true;
        };
        detector.FixationEnded += () =>
        {
            if (open)
            {
                runs.Add(new FixationRun(anchor, onsetUs, currentUs, EndedByLeave: true));
                open = false;
            }
        };

        long lastFedUs = 0;
        bool fed = false;
        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;
            currentUs = frame.Raw.TimestampMicroseconds;
            detector.Update(frame.Filtered, currentUs);
            lastFedUs = currentUs;
            fed = true;
        }

        // A run still open at the end of the stream has no FixationEnded: its hold is knowable up to
        // the last sample it was fed, and it must not be silently dropped — a long final hold is a
        // real hold. It carries EndedByLeave: false so it cannot be counted as a transition.
        if (open && fed)
            runs.Add(new FixationRun(anchor, onsetUs, lastFedUs, EndedByLeave: false));

        return runs;
    }

    /// <summary>
    /// Extracts held episodes under the pinned I-DT definition: accumulate non-gap filtered samples
    /// while the bounding box's diagonal stays within <paramref name="dispersionPx"/>; the sample that
    /// would exceed it starts the next episode. Keep episodes lasting at least a second.
    /// <para>
    /// The bbox diagonal is the debate's dispersion measure (not the running-mean radius): it is the
    /// standard I-DT statistic, and it bounds how far apart the two extreme corners of the hold are.
    /// The median is component-wise, so one long tail does not drag "where the episode was" off it.
    /// </para>
    /// </summary>
    public static List<HeldEpisode> ExtractHeldEpisodes(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        double dispersionPx)
    {
        var episodes = new List<HeldEpisode>();
        var xs = new List<double>();
        var ys = new List<double>();
        long startUs = 0, lastUs = 0, lastValidUs = 0;
        bool haveLastValid = false;
        double minX = 0, maxX = 0, minY = 0, maxY = 0;

        void Close()
        {
            if (xs.Count > 0 && (lastUs - startUs) / 1000.0 >= EpisodeMinDurationMs)
                episodes.Add(new HeldEpisode(startUs, lastUs, Median(xs, ys), xs.Count, BoxDiagonal(minX, maxX, minY, maxY)));
            xs.Clear();
            ys.Clear();
        }

        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;

            long ts = frame.Raw.TimestampMicroseconds;
            double x = frame.Filtered.X, y = frame.Filtered.Y;

            // A long dropout is not held gaze however still the two ends look.
            if (haveLastValid && (ts - lastValidUs) / 1000.0 > EpisodeGapBreakMs)
                Close();

            if (xs.Count == 0)
            {
                xs.Add(x); ys.Add(y);
                startUs = ts;
                minX = maxX = x; minY = maxY = y;
            }
            else
            {
                double nextMinX = Math.Min(minX, x), nextMaxX = Math.Max(maxX, x);
                double nextMinY = Math.Min(minY, y), nextMaxY = Math.Max(maxY, y);
                if (BoxDiagonal(nextMinX, nextMaxX, nextMinY, nextMaxY) <= dispersionPx)
                {
                    xs.Add(x); ys.Add(y);
                    minX = nextMinX; maxX = nextMaxX; minY = nextMinY; maxY = nextMaxY;
                }
                else
                {
                    Close();
                    xs.Add(x); ys.Add(y);
                    startUs = ts;
                    minX = maxX = x; minY = maxY = y;
                }
            }

            lastUs = ts;
            lastValidUs = ts;
            haveLastValid = true;
        }

        Close();
        return episodes;
    }

    private static double BoxDiagonal(double minX, double maxX, double minY, double maxY)
    {
        double w = maxX - minX, h = maxY - minY;
        return Math.Sqrt((w * w) + (h * h));
    }

    private static PhysicalPoint Median(List<double> xs, List<double> ys)
    {
        var sx = xs.OrderBy(v => v).ToArray();
        var sy = ys.OrderBy(v => v).ToArray();
        return new PhysicalPoint(Central(sx), Central(sy));

        static double Central(double[] sorted) => sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    /// <summary>
    /// The served/elsewhere/Midas numbers at one dispersion: an activatable fixation is associated with
    /// an episode when its onset falls in [episode start, episode start + <see cref="ServedOnsetWindowMs"/>].
    /// </summary>
    private static GateRow MeasureAt(
        IReadOnlyList<HeldEpisode> episodes,
        IReadOnlyList<FixationRun> activatable,
        double minutes,
        double dispersionPx,
        double servedDistancePx)
    {
        int served = 0, elsewhere = 0;
        foreach (var episode in episodes)
        {
            bool inWindow = false, near = false;
            foreach (var run in activatable)
            {
                if (!InOnsetWindow(run.OnsetUs, episode)) continue;
                inWindow = true;
                if (run.Anchor.DistanceTo(episode.Median) <= servedDistancePx) near = true;
            }

            if (near) served++;
            else if (inWindow) elsewhere++;
        }

        int midas = activatable.Count(run => !episodes.Any(ep => InOnsetWindow(run.OnsetUs, ep)));
        return new GateRow(dispersionPx, episodes.Count, served, elsewhere, midas, minutes);
    }

    private static bool InOnsetWindow(long onsetUs, HeldEpisode episode) =>
        onsetUs >= episode.StartUs && onsetUs <= episode.StartUs + (ServedOnsetWindowMs * 1000L);

    // ── Report ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the baseline table: every candidate at both dispersions, with the gate verdicts inline.
    /// One table, both metric families — the format the debate agreed on.
    /// </summary>
    /// <param name="inputLabel">What was replayed (path, or the fixture's name).</param>
    /// <param name="analyses">One analysis per candidate, in report order.</param>
    public static string BuildReport(string inputLabel, IReadOnlyList<FixationAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Fixation replay gates — ").AppendLine(inputLabel);
        if (analyses.Count > 0)
        {
            var first = analyses[0];
            sb.Append("  stream: ")
              .Append(first.SampleCount.ToString(ci)).Append(" samples, ")
              .Append(first.DurationSeconds.ToString("0.0", ci)).Append(" s (")
              .Append(first.Minutes.ToString("0.00", ci)).Append(" min), invalid ")
              .Append((first.InvalidFraction * 100).ToString("0.0", ci)).AppendLine(" %");
        }

        sb.AppendLine();
        sb.AppendLine("  candidate              D    episodes  served  G1      bar   G2      midas/m  M1/m   M2/m   M4/m   TTF(s)");
        sb.AppendLine("  ---------------------  ---  --------  ------  ------  ----  ------  -------  -----  -----  -----  ------");
        foreach (var a in analyses)
        {
            AppendRow(sb, a, a.At150);
            AppendRow(sb, a, a.At200);
        }

        // The "new gates fail on old code" guard, stated in the report rather than left to the reader.
        sb.AppendLine();
        foreach (var a in analyses)
        {
            sb.Append("  ").Append(a.Candidate.Name).Append(": G1 ")
              .Append(a.At150.ServedPasses ? "passes" : "FAILS").Append(" at D150 (")
              .Append(a.At150.ServedShare.ToString("P0", ci)).Append(" vs ")
              .Append(a.At150.ServedBar.ToString("P0", ci)).Append("), ")
              .Append(a.At200.ServedPasses ? "passes" : "FAILS").Append(" at D200 (")
              .Append(a.At200.ServedShare.ToString("P0", ci)).Append(" vs ")
              .Append(a.At200.ServedBar.ToString("P0", ci)).AppendLine(")");
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, FixationAnalysis a, GateRow row)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.Append("  ").Append(a.Candidate.Name.PadRight(21))
          .Append(' ').Append(row.DispersionPx.ToString("0", ci).PadLeft(3))
          .Append("  ").Append(row.EpisodeCount.ToString(ci).PadLeft(8))
          .Append("  ").Append(row.ServedEpisodes.ToString(ci).PadLeft(6))
          .Append("  ").Append(row.ServedShare.ToString("P0", ci).PadLeft(6))
          .Append("  ").Append(row.ServedBar.ToString("P0", ci).PadLeft(4))
          .Append("  ").Append(row.ElsewhereShare.ToString("P0", ci).PadLeft(6))
          .Append("  ").Append(row.MidasPerMinute.ToString("0.0", ci).PadLeft(7))
          .Append("  ").Append(a.ActivatablePerMinute.ToString("0.0", ci).PadLeft(5))
          .Append("  ").Append(a.KeyboardPerMinute.ToString("0.0", ci).PadLeft(5))
          .Append("  ").Append(a.FlickerPerMinute.ToString("0.0", ci).PadLeft(5))
          .Append("  ").Append((a.TimeToFirstFixationSeconds?.ToString("0.00", ci) ?? "n/a").PadLeft(6))
          .AppendLine();
    }
}
