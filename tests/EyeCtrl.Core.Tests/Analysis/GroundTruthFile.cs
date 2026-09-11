namespace EyeCtrl.Core.Tests.Analysis;

using System.Text.Json;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// Reads the sidecar <see cref="ScriptedGroundTruthRecorder"/> writes, and slices a replay by its
/// windows so each scripted target can be scored against the position the user was actually asked to
/// hold.
/// <para>
/// Offline only, beside the analyzer — nothing at runtime reads a ground-truth sidecar, exactly as
/// nothing at runtime reads a recording.
/// </para>
/// </summary>
public static class GroundTruthFile
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Reads a sidecar from disk.</summary>
    public static GroundTruthSession Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a sidecar from JSON.</summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the payload is empty or carries a schema this build does not understand. Refusing
    /// is the point: silently reading an unknown shape would produce a ground truth that is not one,
    /// and every absolute-accuracy number computed from it would be wrong without looking wrong.
    /// </exception>
    public static GroundTruthSession Parse(string json)
    {
        var session = JsonSerializer.Deserialize<GroundTruthSession>(json, JsonOptions)
            ?? throw new InvalidOperationException("Ground-truth sidecar is empty.");

        if (session.SchemaVersion != GroundTruthSession.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Ground-truth sidecar schema {session.SchemaVersion} is not supported " +
                $"(this build reads {GroundTruthSession.CurrentSchemaVersion}).");

        return session;
    }

    /// <summary>
    /// The replay frames that fall inside one scripted window, i.e. the samples recorded while the
    /// user was holding that target, from arrival onward.
    /// </summary>
    /// <param name="frames">A full replay of the recording.</param>
    /// <param name="window">One scripted target's window.</param>
    public static List<ReplayPipeline.Frame> Slice(
        IReadOnlyList<ReplayPipeline.Frame> frames, GroundTruthWindow window)
    {
        var segment = new List<ReplayPipeline.Frame>();
        foreach (var frame in frames)
            if (window.Contains(frame.Raw.TimestampMicroseconds))
                segment.Add(frame);
        return segment;
    }

    /// <summary>One scripted target's absolute accuracy, scored against its known position.</summary>
    /// <param name="Window">The scripted window scored.</param>
    /// <param name="Deviation">
    /// Radial deviation from the TARGET — absolute accuracy, not the self-referential deviation a
    /// free-view recording is limited to.
    /// </param>
    public readonly record struct ScoredTarget(GroundTruthWindow Window, GazeMetrics.FixationDeviation Deviation);

    /// <summary>
    /// Scores every scripted target in a session against a replay of the recording it accompanies.
    /// <para>
    /// This is the end-to-end path the ticket exists to enable: raw recording + sidecar → replay →
    /// absolute p95/RMS per target. Windows that caught no frames are skipped rather than scored as
    /// zero, since "no data" and "perfect" must not look alike.
    /// </para>
    /// </summary>
    public static List<ScoredTarget> Score(
        IReadOnlyList<ReplayPipeline.Frame> frames, GroundTruthSession session)
    {
        var scored = new List<ScoredTarget>();
        foreach (var window in session.Windows)
        {
            var segment = Slice(frames, window);

            // Two ways a window can carry nothing scorable, and both must be skipped rather than
            // scored. No frames at all is obvious. The subtle one is a window whose frames are ALL
            // gap-held — a target the user blinked through, or one shown entirely during tracking
            // loss: MeasureFixationDeviation excludes held samples, so it would return (0, 0, 0) and
            // that window would report as PERFECT accuracy on no data whatsoever. "No data" and
            // "flawless" must never look alike in a report that drives a roadmap decision.
            if (segment.Count == 0 || !segment.Any(f => !f.IsInGap)) continue;

            scored.Add(new ScoredTarget(window, GazeMetrics.MeasureFixationDeviation(segment, window.Target)));
        }
        return scored;
    }

    /// <summary>How a sidecar lines up with the recording it is supposed to describe.</summary>
    /// <param name="WindowsInsideRecording">Windows whose scoring span falls within the recorded stream.</param>
    /// <param name="WindowsOutsideRecording">Windows that do not — the signature of a STALE sidecar.</param>
    /// <param name="IsStale">
    /// True when no window overlaps the recording at all: the sidecar describes a different session.
    /// </param>
    public readonly record struct Alignment(int WindowsInsideRecording, int WindowsOutsideRecording, bool IsStale)
    {
        /// <summary>A human-readable verdict for the operator's post-run check.</summary>
        public string Describe() => IsStale
            ? $"STALE ground truth: none of its {WindowsOutsideRecording} windows overlap this recording — "
            + "it almost certainly describes an earlier run. Re-collect before measuring."
            : WindowsOutsideRecording > 0
                ? $"PARTIAL overlap: {WindowsInsideRecording} of {WindowsInsideRecording + WindowsOutsideRecording} "
                + "windows fall inside the recording; the rest are from another session or a cancelled run."
                : $"aligned: all {WindowsInsideRecording} windows fall inside the recording.";
    }

    /// <summary>
    /// Checks a sidecar against the recording's own time span.
    /// <para>
    /// Exists because a cancelled run deliberately writes no sidecar — which leaves the PREVIOUS run's
    /// sidecar sitting beside a recording that has since grown. The operator's "~9 windows" check then
    /// passes on stale ground truth, and every absolute number computed from it silently describes the
    /// wrong session. Deleting on cancel would be worse: it would destroy a sidecar that still
    /// correctly describes samples present in the CSV. So the file is left alone and the mismatch is
    /// detected here, where the two are read together and the device timestamps can be compared.
    /// </para>
    /// </summary>
    /// <param name="session">The sidecar.</param>
    /// <param name="samples">The recording's raw samples.</param>
    public static Alignment CheckAlignment(GroundTruthSession session, IReadOnlyList<GazePoint> samples)
    {
        if (samples.Count == 0 || session.Windows.Count == 0)
            return new Alignment(0, session.Windows.Count, IsStale: session.Windows.Count > 0);

        long first = samples[0].TimestampMicroseconds;
        long last = samples[^1].TimestampMicroseconds;

        int inside = 0, outside = 0;
        foreach (var window in session.Windows)
        {
            var (from, to) = window.ScoringWindow;
            // Overlap, not containment: a run whose recording started a moment late still has usable
            // windows, and demanding full containment would reject them.
            if (to >= first && from <= last) inside++;
            else outside++;
        }

        return new Alignment(inside, outside, IsStale: inside == 0);
    }
}
