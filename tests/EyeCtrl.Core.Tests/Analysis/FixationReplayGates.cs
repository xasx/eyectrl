namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// A fixation rule under measurement: the detector under test, driven sample by sample with the
/// already-filtered, gap-resolved stream, reporting its onset, its activation and its release.
/// <para>
/// This seam exists so the gate can score a NEW rule (ticket 3's running centroid) without being
/// edited, while "today" stays today's code (<see cref="LegacyFixationRule"/> over the frozen
/// <see cref="LegacyFixationDetector"/>). The harness never re-implements the rule: it reads these
/// events only.
/// </para>
/// <para>
/// <see cref="Update"/> is called for every NON-GAP frame, mirroring
/// <c>GazeEngine.ProcessGazePoint</c>'s <c>!isInGap</c> feed. <c>isValid</c> is the raw sample's
/// validity, which that guard does not consult — the phantom-(0,0) behaviour of the frozen rule
/// depends on seeing the same frames the engine feeds it.
/// </para>
/// </summary>
public interface IFixationRule
{
    /// <summary>Fired when the rule reports a fixation onset: the activation point and the sample time.</summary>
    event Action<PhysicalPoint, long>? FixationStarted;

    /// <summary>Fired when the rule's activation clock completes: the sample time at completion.</summary>
    event Action<long>? FixationActivated;

    /// <summary>Fired when the rule releases a run it had reported: the last sample it credited, the
    /// valid-time age it accrued, and whether the release was the gaze leaving (false = stream end).</summary>
    event Action<FixationRelease>? FixationReleased;

    /// <summary>Feeds one non-gap sample. <paramref name="timestampUs"/> is the raw sample's time.</summary>
    void Update(PhysicalPoint point, long timestampUs, bool isValid);

    /// <summary>Ends the stream, releasing a run still open at the last fed sample.</summary>
    void CompleteStream();
}

/// <summary>What a rule says when it releases a run.</summary>
/// <param name="LastCreditedUs">Timestamp of the last sample the rule credited as part of the run.</param>
/// <param name="CreditedAgeUs">Valid-time age accrued from the run's anchor to that sample.</param>
/// <param name="EndedByLeave">True when gaze left; false when the stream ended while the run was open.</param>
public readonly record struct FixationRelease(long LastCreditedUs, long CreditedAgeUs, bool EndedByLeave);

/// <summary>
/// The fixation debate's acceptance gates, executable on any stream: fixation runs from a rule under
/// test, held episodes from the pinned I-DT definition, and the G1-G6 metrics computed from the two.
/// <para>
/// This is the ONE committed instrument the later tickets measure against. Everything is pinned: the
/// I-DT definition, the gate bars, and the legacy rule (<see cref="TodayAtRadius"/>) that "today"
/// means.
/// </para>
/// <para>
/// Why the replay re-creates the engine's feed (<c>if (!SuppressFixation &amp;&amp; !isInGap)</c>) rather
/// than driving the real <c>GazeEngine</c> for the metrics: the engine's fixation rule is private and
/// its per-sample anchor state is unobservable, so runs cannot be reconstructed from its public
/// surface. The FEED is still pinned, and it is: <c>ReplaySliceFidelityTests</c> asserts, on the
/// fixture and on the recording, that the engine's onsets and ends — observed through
/// <c>InteractionManager.ShowActivationButton</c>/<c>HideActivationButton</c>, which fire synchronously
/// inside <c>Push</c> — equal the harness's runs, and that the filtered point and gap flag match on
/// every sample. A change to <c>GazeEngine.cs:604-605</c> (feeding raw points, or gap frames) breaks
/// that parity instead of silently changing what the gates measure. The rule seam
/// (<see cref="IFixationRule"/>) is what ticket 3 plugs its new rule into. What replay cannot see:
/// suppression, state, the dispatcher. The live engine feeds the detector in every non-paused state
/// with <c>SuppressFixation</c> false, so the replay is that steady state.
/// </para>
/// </summary>
public static class FixationReplayGates
{
    // ── The pinned definition ─────────────────────────────────────────────────
    // Every constant below comes from the final synthesis, the designer's cross-review, or the
    // fixup's refinement of them; none is a tuning knob.

    /// <summary>Fixation-detection time (<c>DwellSettings</c> default). The activation clock starts only when a run is reported.</summary>
    public const int FixationDetectionMs = 300;

    /// <summary>Activation-button dwell (<c>DwellSettings</c> default). A run is "activatable" when its age reaches fix + this.</summary>
    public const int ActivationButtonDwellMs = 700;

    /// <summary>Keyboard-key dwell (<c>DwellSettings</c> default) — the M2 "keyboard-length hold" threshold.</summary>
    public const int KeyboardKeyDwellMs = 450;

    /// <summary>
    /// Per-sample credit cap for the valid-time age: about 2 frames at the recording's 33 Hz, so a
    /// dropout can never credit more than a sample or two. Ticket 3 pins the production constant; the
    /// harness matches "about 2 frames" and records the baseline it produced.
    /// </summary>
    public const int AgeCreditCapMs = 66;

    /// <summary>
    /// The served@place bound in physical pixels: how far the activation point may sit from the held
    /// episode's median and still count as serving it. 100 px is the debate's shared constant.
    /// </summary>
    public const double ServedDistancePx = 100;

    /// <summary>
    /// How late after a held episode's start its serving fixation may still have started. One second of
    /// episode plus 300 ms of detection slack; the debate's "within 1.3 s". This bounds SERVICE only —
    /// G3's idle-trigger test uses the episode span instead (see <see cref="MeasureAt"/>).
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
    /// leaving, matching the 500 ms pause rule the debate adopted for fixation age.
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

    /// <summary>One candidate rule under measurement: a rule factory and a filter factory.</summary>
    /// <param name="Name">Label used in the report.</param>
    /// <param name="CreateRule">Factory for a FRESH rule; replay must never reuse rule state across runs.</param>
    /// <param name="CreateFilter">Factory for a FRESH filter; replay must never reuse filter state across runs.</param>
    /// <param name="MaxGapMs">The <c>MaxGapInterpolationMs</c> this candidate runs with.</param>
    public sealed record Candidate(
        string Name,
        Func<IFixationRule> CreateRule,
        Func<IGazeFilter> CreateFilter,
        int MaxGapMs = MaxGapInterpolationMs);

    /// <summary>
    /// Today's shipped rule at <paramref name="radiusPixels"/>: the frozen first-sample-anchor detector
    /// with today's wall-clock onset, plus the retuned One-Euro filter and the shipped gap budget.
    /// </summary>
    public static Candidate TodayAtRadius(double radiusPixels) =>
        new($"today's rule, r{radiusPixels:0}",
            () => new LegacyFixationRule(radiusPixels),
            () => new OneEuroFilter());

    /// <summary>Today's rule at r50 — the radius the user's eye sessions actually ran (skeptic's log reconstruction).</summary>
    public static Candidate TodayR50 { get; } = TodayAtRadius(50);

    /// <summary>Today's rule at r75 — ticket 3's candidate radius, and the middle point of the sweep.</summary>
    public static Candidate TodayR75 { get; } = TodayAtRadius(75);

    /// <summary>Today's rule at r90 — the value in <c>settings.json</c> at the last checkpoint.</summary>
    public static Candidate TodayR90 { get; } = TodayAtRadius(90);

    /// <summary>One fixation run, exactly as the rule reported it.</summary>
    /// <param name="Anchor">The activation point: the point the rule reported at onset.</param>
    /// <param name="OnsetUs">Timestamp of the sample on which the rule reported onset — when the button would appear.</param>
    /// <param name="EndUs">Timestamp of the last sample the rule credited to the run.</param>
    /// <param name="CreditedAgeUs">Valid-time age accrued from the run's anchor to <see cref="EndUs"/>.</param>
    /// <param name="ActivationUs">Timestamp at which the rule's activation clock completed, or null.</param>
    /// <param name="EndedByLeave">False when the stream ended while the run was still open.</param>
    public sealed record FixationRun(
        PhysicalPoint Anchor,
        long OnsetUs,
        long EndUs,
        long CreditedAgeUs,
        long? ActivationUs,
        bool EndedByLeave)
    {
        /// <summary>True when the rule reported activation — the M1 "activatable fixation".</summary>
        public bool IsActivatable => ActivationUs is not null;

        /// <summary>True when the credited age reaches the keyboard-length threshold — M2 (G5).</summary>
        public bool IsKeyboardLength => CreditedAgeUs >= (FixationDetectionMs + KeyboardKeyDwellMs) * 1000L;
    }

    /// <summary>A held episode under the pinned I-DT definition: one low-dispersion run lasting at least one second.</summary>
    /// <param name="StartUs">Timestamp of the episode's first sample.</param>
    /// <param name="EndUs">Timestamp of the episode's last sample.</param>
    /// <param name="Median">Component-wise median of the episode's filtered points; the place the episode is "at".</param>
    /// <param name="SampleCount">Samples that contributed (gap-held samples never do).</param>
    /// <param name="BoxDiagonalPx">The episode's bounding-box diagonal — the dispersion it was held within.</param>
    public sealed record HeldEpisode(long StartUs, long EndUs, PhysicalPoint Median, int SampleCount, double BoxDiagonalPx)
    {
        /// <summary>Whether a timestamp falls inside the episode's own span — G3's containment test.</summary>
        public bool Contains(long timestampUs) => timestampUs >= StartUs && timestampUs <= EndUs;
    }

    /// <summary>The gate numbers at one dispersion threshold for one candidate.</summary>
    /// <param name="DispersionPx">The I-DT dispersion (D150 or D200).</param>
    /// <param name="EpisodeCount">Held episodes at this dispersion.</param>
    /// <param name="ServedEpisodes">Episodes with an activatable fixation in the onset window at ≤ <see cref="ServedDistancePx"/> from the median.</param>
    /// <param name="ElsewhereEpisodes">Episodes whose only in-window activatable fixations are misplaced.</param>
    /// <param name="MidasRuns">Activatable fixations that start in no held episode span — the idle-trigger proxy.</param>
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
    }

    /// <summary>One gate's verdict. <see cref="Passed"/> is null when the gate cannot be evaluated.</summary>
    public sealed record GateVerdict(string Gate, bool? Passed, string Detail);

    /// <summary>One report row: what was measured, and the today's-rule analysis G4/G5 compare against.</summary>
    public sealed record ReportRow(FixationAnalysis Analysis, FixationAnalysis? Baseline);

    // ── Replay ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replays a parsed recording through the real gap interpolator + filter, then through the
    /// candidate's rule, and computes the gate numbers. Validates the recording for replay, waiving
    /// ONLY the missing-trailer refusal (see <see cref="EnsureReplayable"/>).
    /// </summary>
    public static FixationAnalysis Analyze(
        GazeRecordingFile.Recording recording,
        Candidate candidate,
        double servedDistancePx = ServedDistancePx)
    {
        EnsureReplayable(recording);
        return Analyze(recording.Samples, candidate, recording.Header.CalibrationTransform, servedDistancePx);
    }

    /// <summary>
    /// Validates a parsed recording for replay, waiving ONLY the missing-trailer refusal: the user's
    /// recording was rotated while the app ran, so it has no closing trailer, and its samples are
    /// still exactly what the pipeline saw. Every other refusal stays: malformed lines, dropped
    /// samples and an un-serializable calibration all mean the file no longer describes that stream.
    /// </summary>
    public static void EnsureReplayable(GazeRecordingFile.Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.MalformedLines > 0)
            throw new InvalidOperationException(
                $"This gaze recording cannot be analyzed: {recording.MalformedLines} line(s) could not be parsed, so the file is truncated or corrupt.");
        if (recording.Trailer is { DroppedSamples: > 0 } trailer)
            throw new InvalidOperationException(
                $"This gaze recording cannot be analyzed: the recorder dropped {trailer.DroppedSamples} of {trailer.RecordedSamples} samples, so the holes are indistinguishable from tracker dropouts.");
        if (recording.Header.CalibrationActive && recording.Header.CalibrationTransform is null)
            throw new InvalidOperationException(
                "This gaze recording cannot be analyzed: the session ran with a calibration whose transform the recorder could not serialize, so replay cannot reproduce the corrected stream.");
    }

    /// <summary>
    /// Replays a raw stream through the real gap interpolator + filter, then through the candidate's
    /// rule, and computes the gate numbers. Deterministic: no clock, no device, no UI.
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

        var runs = DetectRuns(frames, candidate);
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
    /// Drives a fresh candidate rule with every non-gap filtered sample (the engine's feed) and
    /// captures its events as runs. Mirrors <c>GazeEngine.ProcessGazePoint</c>:
    /// <c>if (!SuppressFixation &amp;&amp; !isInGap)</c>, with no suppression in a replay.
    /// </summary>
    public static List<FixationRun> DetectRuns(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(candidate);

        var rule = candidate.CreateRule();
        var runs = new List<FixationRun>();
        RunBuilder? open = null;

        rule.FixationStarted += (point, timestampUs) =>
        {
            // A second onset before a release means the adapter dropped a run. Silently replacing the
            // open one would UNDER-count flicker, which makes G4 pass spuriously — fail loudly instead.
            if (open is not null)
                throw new InvalidOperationException(
                    "The rule reported a second fixation onset before releasing the first; runs would be dropped and flicker under-counted.");
            open = new RunBuilder(point, timestampUs);
        };
        rule.FixationActivated += timestampUs =>
        {
            if (open is null)
                throw new InvalidOperationException("The rule reported an activation without a reported onset.");
            open.Activate(timestampUs);
        };
        rule.FixationReleased += release =>
        {
            if (open is null)
                throw new InvalidOperationException("The rule released a run it never reported an onset for.");
            runs.Add(open.Build(release));
            open = null;
        };

        foreach (var frame in frames)
        {
            if (frame.IsInGap) continue;
            rule.Update(frame.Filtered, frame.Raw.TimestampMicroseconds, frame.Raw.IsValid);
        }

        // A run still open at the end of the stream has no leaving sample: the rule releases it with
        // whichever samples it did credit, so a long final hold is not silently dropped.
        rule.CompleteStream();
        if (open is not null)
            throw new InvalidOperationException("The rule left a run unreleased at the end of the stream.");
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
    /// The served/elsewhere/Midas numbers at one dispersion. Service associates an activatable fixation
    /// with an episode by the ONSET WINDOW [episode start, start + <see cref="ServedOnsetWindowMs"/>];
    /// G3's idle test asks whether the onset falls in the episode's OWN SPAN, which is the debate's
    /// definition and does not penalise a rule that holds longer inside an episode.
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

        int midas = activatable.Count(run => !episodes.Any(episode => episode.Contains(run.OnsetUs)));
        return new GateRow(dispersionPx, episodes.Count, served, elsewhere, midas, minutes);
    }

    private static bool InOnsetWindow(long onsetUs, HeldEpisode episode) =>
        onsetUs >= episode.StartUs && onsetUs <= episode.StartUs + (ServedOnsetWindowMs * 1000L);

    // ── Verdicts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates every declared gate for a candidate. G1/G2/G6 need only the candidate; G4 (flicker
    /// ≤ +25 %) and G5 (keyboard holds not lower) compare against <paramref name="baseline"/> — the
    /// frozen legacy rule at the SAME radius — and are reported as not-evaluated when it is null.
    /// </summary>
    public static List<GateVerdict> EvaluateGates(FixationAnalysis candidate, FixationAnalysis? baseline)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var ci = CultureInfo.InvariantCulture;
        var verdicts = new List<GateVerdict>
        {
            new("G1 D150", candidate.At150.ServedPasses,
                $"{candidate.At150.ServedShare.ToString("P0", ci)} vs bar {candidate.At150.ServedBar.ToString("P0", ci)}"),
            new("G1 D200", candidate.At200.ServedPasses,
                $"{candidate.At200.ServedShare.ToString("P0", ci)} vs bar {candidate.At200.ServedBar.ToString("P0", ci)}"),
            new("G2 D150", candidate.At150.ElsewherePasses,
                $"{candidate.At150.ElsewhereShare.ToString("P0", ci)} vs cap {G2Bar.ToString("P0", ci)}"),
            new("G2 D200", candidate.At200.ElsewherePasses,
                $"{candidate.At200.ElsewhereShare.ToString("P0", ci)} vs cap {G2Bar.ToString("P0", ci)}"),
        };

        if (baseline is null)
        {
            verdicts.Add(new("G4", null, "no baseline supplied — G4 compares against today's rule at the same R"));
            verdicts.Add(new("G5", null, "no baseline supplied — G5 compares against today's rule at the same R"));
        }
        else
        {
            double cap = G4FlickerCap * baseline.FlickerPerMinute;
            verdicts.Add(new("G4", candidate.FlickerPerMinute <= cap,
                $"{candidate.FlickerPerMinute.ToString("0.0", ci)}/min vs cap {cap.ToString("0.0", ci)}/min (=+{((G4FlickerCap - 1) * 100):0}% of today)"));
            verdicts.Add(new("G5", candidate.KeyboardPerMinute >= baseline.KeyboardPerMinute,
                $"{candidate.KeyboardPerMinute.ToString("0.0", ci)}/min vs today {baseline.KeyboardPerMinute.ToString("0.0", ci)}/min"));
        }

        verdicts.Add(new("G6", candidate.TimeToFirstFixationPasses,
            candidate.TimeToFirstFixationSeconds is { } ttf
                ? $"{ttf.ToString("0.00", ci)}s vs bar {G6BarSeconds.ToString("0.0", ci)}s"
                : "no fixation formed"));
        return verdicts;
    }

    // ── Report ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the table: every row at both dispersions, plus its gate verdicts. Each row carries its
    /// own baseline (the frozen legacy rule at the same radius) for G4/G5; a baseline-table row points
    /// at itself, which is the honest self-comparison.
    /// </summary>
    /// <param name="inputLabel">What was replayed (path, or the fixture's name).</param>
    /// <param name="rows">One row per candidate, in report order.</param>
    public static string BuildReport(string inputLabel, IReadOnlyList<ReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Fixation replay gates — ").AppendLine(inputLabel);
        if (rows.Count > 0)
        {
            var first = rows[0].Analysis;
            sb.Append("  stream: ")
              .Append(first.SampleCount.ToString(ci)).Append(" samples, ")
              .Append(first.DurationSeconds.ToString("0.0", ci)).Append(" s (")
              .Append(first.Minutes.ToString("0.00", ci)).Append(" min), invalid ")
              .Append((first.InvalidFraction * 100).ToString("0.0", ci)).AppendLine(" %");
        }

        sb.AppendLine();
        sb.AppendLine("  candidate              D    episodes  served  G1      bar   G2      midas/m  M1/m   M2/m   M4/m   TTF(s)");
        sb.AppendLine("  ---------------------  ---  --------  ------  ------  ----  ------  -------  -----  -----  -----  ------");
        foreach (var row in rows)
        {
            AppendRow(sb, row.Analysis, row.Analysis.At150);
            AppendRow(sb, row.Analysis, row.Analysis.At200);
        }

        sb.AppendLine();
        sb.Append("  G3 target: <= ").Append(G3TargetPerMinute.ToString("0.0", ci)).AppendLine("/min (reported, not gating)");
        foreach (var row in rows)
        {
            sb.Append("  ").Append(row.Analysis.Candidate.Name).AppendLine(":");
            foreach (var verdict in EvaluateGates(row.Analysis, row.Baseline))
            {
                sb.Append("    ").Append(verdict.Gate.PadRight(8))
                  .Append(verdict.Passed switch { true => "pass", false => "FAIL", null => "n/a " })
                  .Append("  ").AppendLine(verdict.Detail);
            }
        }
        return sb.ToString();
    }

    /// <summary>Renders the table with one shared baseline for G4/G5, or none.</summary>
    public static string BuildReport(string inputLabel, IReadOnlyList<FixationAnalysis> analyses, FixationAnalysis? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        return BuildReport(inputLabel, analyses.Select(a => new ReportRow(a, baseline)).ToList());
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

    private sealed class RunBuilder(PhysicalPoint anchor, long onsetUs)
    {
        private long? _activationUs;

        public void Activate(long timestampUs) => _activationUs ??= timestampUs;

        public FixationRun Build(FixationRelease release) =>
            new(anchor, onsetUs, release.LastCreditedUs, release.CreditedAgeUs, _activationUs, release.EndedByLeave);
    }
}

/// <summary>
/// Today's rule under ticket 3's clock: the frozen <see cref="LegacyFixationDetector"/>'s geometry
/// (first-sample anchor, one sample out ends it) plus the valid-time age ledger ticket 3 defines —
/// per-sample dt capped at <see cref="FixationReplayGates.AgeCreditCapMs"/>, accrued only on valid
/// in-radius samples, to the LAST in-radius sample rather than the sample that left. This is the
/// refined measurement the fixup pinned: the debate's harnesses stopped the hold one frame late.
/// </summary>
public sealed class LegacyFixationRule : IFixationRule
{
    private readonly LegacyFixationDetector _detector;
    private readonly long _activationAgeUs;
    private readonly long _maxDtCreditUs;

    private long _anchorUs;
    private long _previousFedUs;
    private long _creditedUs;
    private long _lastCreditedUs;
    private long _currentTs;
    private bool _onsetFired;
    private bool _activated;

    /// <summary>Creates the rule at a radius, with the pinned detection time and credit cap.</summary>
    public LegacyFixationRule(
        double radiusPixels,
        int fixationMs = FixationReplayGates.FixationDetectionMs,
        int ageCreditCapMs = FixationReplayGates.AgeCreditCapMs)
    {
        _detector = new LegacyFixationDetector(radiusPixels, fixationMs);
        _activationAgeUs = (fixationMs + FixationReplayGates.ActivationButtonDwellMs) * 1000L;
        _maxDtCreditUs = ageCreditCapMs * 1000L;

        _detector.FixationStarted += point =>
        {
            _onsetFired = true;
            FixationStarted?.Invoke(point, _currentTs);
        };
        _detector.FixationEnded += () =>
        {
            if (_onsetFired)
                FixationReleased?.Invoke(new FixationRelease(_lastCreditedUs, _creditedUs, EndedByLeave: true));
            _onsetFired = false;
        };
    }

    /// <inheritdoc />
    public event Action<PhysicalPoint, long>? FixationStarted;

    /// <inheritdoc />
    public event Action<long>? FixationActivated;

    /// <inheritdoc />
    public event Action<FixationRelease>? FixationReleased;

    /// <inheritdoc />
    public void Update(PhysicalPoint point, long timestampUs, bool isValid)
    {
        _currentTs = timestampUs;
        long previousAnchorUs = _detector.AnchorTimestamp;

        _detector.Update(point, timestampUs);

        if (_detector.AnchorTimestamp != previousAnchorUs)
        {
            // A new run: first sample of the stream, or the sample that ended the previous run. The
            // anchor sample itself credits zero age; a new run starts clean.
            _anchorUs = _detector.AnchorTimestamp;
            _creditedUs = 0;
            _lastCreditedUs = _anchorUs;
            _onsetFired = false;
            _activated = false;
        }
        else if (isValid && timestampUs > _anchorUs)
        {
            long dt = timestampUs - _previousFedUs;
            if (dt > 0)
            {
                _creditedUs += Math.Min(dt, _maxDtCreditUs);
                _lastCreditedUs = timestampUs;
                if (!_activated && _creditedUs >= _activationAgeUs)
                {
                    _activated = true;
                    FixationActivated?.Invoke(timestampUs);
                }
            }
        }

        _previousFedUs = timestampUs;
    }

    /// <inheritdoc />
    public void CompleteStream()
    {
        if (_onsetFired)
            FixationReleased?.Invoke(new FixationRelease(_lastCreditedUs, _creditedUs, EndedByLeave: false));
    }
}
