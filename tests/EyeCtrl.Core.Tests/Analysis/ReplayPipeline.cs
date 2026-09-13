namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Replays a raw gaze stream through a candidate pipeline and emits, per sample, exactly what the
/// pipeline produced. This is the harness every metric is computed from.
/// <para>
/// Deterministic by construction: it drives the real <see cref="GapInterpolator"/> and
/// <see cref="IGazeFilter"/> with the recording's own timestamps, touches no clock, no device and no
/// UI, and holds no state between runs. The same input yields identical output, which is what makes
/// a before/after comparison of two candidate pipelines meaningful rather than anecdotal.
/// </para>
/// <para>
/// What replay CANNOT see: everything downstream of the gaze thread. There is no dispatcher here, so
/// no UI-delivery latency and no coalescing. Metrics derived from this harness are therefore labelled
/// <c>Replay*</c> throughout — they are the pipeline's contribution, not the user's experience, and
/// conflating the two would let a filter take credit for latency the dispatcher owns.
/// </para>
/// </summary>
public static class ReplayPipeline
{
    /// <summary>One replayed sample: the raw input and everything the pipeline made of it.</summary>
    /// <param name="Raw">The recorded sample, unmodified — the device's own coordinate space.</param>
    /// <param name="Corrected">
    /// The point the filter was actually given: <see cref="Raw"/> after the session's calibration
    /// transform, or the same coordinates when the session ran uncalibrated.
    /// <para>
    /// Kept separate from <see cref="Raw"/> because the two live in different coordinate spaces the
    /// moment a calibration is active, and every input-vs-output metric must use this one. Comparing a
    /// <see cref="Filtered"/> screen-space position against a tracker-space <see cref="Raw"/> yields a
    /// distance that is neither, and in the wrong units by whatever the transform's scale happens to
    /// be — a silently wrong number rather than a visible failure.
    /// </para>
    /// </param>
    /// <param name="Filtered">The pipeline's output position for this sample (held during a gap).</param>
    /// <param name="IsInGap"><c>true</c> when the pipeline reported the position as held rather than measured.</param>
    public readonly record struct Frame(
        GazePoint Raw,
        PhysicalPoint Corrected,
        PhysicalPoint Filtered,
        bool IsInGap);

    /// <summary>A named candidate pipeline: how to build its filter, and its gap budget.</summary>
    /// <param name="Name">Label used in reports, e.g. "baseline (OneEuro 5.0/0.5)".</param>
    /// <param name="CreateFilter">Factory for a FRESH filter — replay must never reuse filter state across runs.</param>
    /// <param name="MaxGapMs">The <c>MaxGapInterpolationMs</c> this candidate runs with.</param>
    public sealed record Candidate(string Name, Func<IGazeFilter> CreateFilter, int MaxGapMs = 150);

    /// <summary>
    /// The pipeline as it shipped BEFORE the Stage 3 retune: <see cref="LegacyOneEuroFilter"/> at
    /// minCutoff 5.0 / pixel-unit beta 0.5, 150 ms gap budget. The "before" half of every comparison.
    /// <para>
    /// Deliberately pinned to the frozen legacy implementation rather than to <c>new OneEuroFilter()</c>:
    /// a baseline that tracks the production filter is not a baseline, and every characterisation number
    /// recorded against it would move with the thing it is supposed to be measuring.
    /// </para>
    /// </summary>
    public static Candidate Baseline { get; } = new(
        "baseline (legacy OneEuro minCutoff 5.0 / beta 0.5 px-units)",
        () => new LegacyOneEuroFilter(),
        MaxGapMs: 150);

    /// <summary>
    /// The Stage 3 candidate: the retuned <see cref="OneEuroFilter"/> at the new shipped defaults
    /// (minCutoff 1.5 Hz, beta 20 Hz per screen-diagonal/s). The "after" half of every comparison.
    /// </summary>
    public static Candidate Retuned { get; } = new(
        "retuned (OneEuro minCutoff 1.5 / beta 20 per-diagonal)",
        () => new OneEuroFilter(),
        MaxGapMs: 150);

    /// <summary>The no-filter reference: measures the raw stream through the same gap handling.</summary>
    public static Candidate Unfiltered { get; } = new(
        "unfiltered (gap handling only)",
        () => new PassThroughFilter(),
        MaxGapMs: 150);

    /// <summary>
    /// Replays a recording through a candidate pipeline, applying the calibration transform the session
    /// actually ran with. This is the entry point analysis should use: it also refuses a recording that
    /// is not complete enough to measure.
    /// </summary>
    public static Frame[] Run(GazeRecordingFile.Recording recording, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(recording);
        recording.EnsureUsable();
        return RunSynthetic(recording.Samples, candidate, recording.Header.CalibrationTransform);
    }

    /// <summary>
    /// Replays a bare stream of samples and returns one frame per input sample, in order.
    /// <para>
    /// For SYNTHETIC streams — <c>GazeStreamGenerator</c>'s, or ones a test builds by hand — which have
    /// no <c>Recording</c> to carry a header, a trailer or a transform. Named apart from
    /// <see cref="Run(GazeRecordingFile.Recording, Candidate)"/> rather than overloading it, because
    /// the two differ in exactly the way a mistake is invisible: this one applies no calibration and
    /// asks no questions about completeness, so pointing it at a real recording's samples silently
    /// measures the tracker's raw coordinate space and silently accepts a file with holes in it. That
    /// had already happened once in this file's own round-trip test. The call site now says which of
    /// the two it is.
    /// </para>
    /// </summary>
    /// <param name="samples">Raw samples, exactly as the device produced them.</param>
    /// <param name="candidate">The pipeline under test.</param>
    /// <param name="calibration">
    /// The session's calibration coefficients, or <c>null</c> for a session that ran uncalibrated.
    /// <para>
    /// Load-bearing, not decoration. <c>GazeEngine.OnGazePoint</c> applies the transform to every VALID
    /// sample before handing it to the interpolator, so a replay that skips it filters the tracker's
    /// raw coordinate space instead of the corrected one. The transform is affine, so it scales the
    /// signal: every distance-valued metric — jitter, overshoot, the settle radius the acceptance gate
    /// vetoes on — comes out multiplied by a factor nothing in the report discloses. That is worse than
    /// having no number, because the number looks fine.
    /// </para>
    /// </param>
    public static Frame[] RunSynthetic(
        IReadOnlyList<GazePoint> samples,
        Candidate candidate,
        AffineTransformParameters? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(candidate);

        var interpolator = new GapInterpolator(candidate.CreateFilter(), candidate.MaxGapMs);
        var frames = new Frame[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var raw = samples[i];

            // Mirrors OnGazePoint exactly, INCLUDING the IsValid guard: an invalid sample carries no
            // position to correct, and transforming its coordinates anyway would feed the interpolator a
            // different garbage value from the one the live pipeline saw.
            var corrected = raw;
            if (raw.IsValid && calibration is { } c)
            {
                corrected = raw with
                {
                    X = (c.A * raw.X) + (c.B * raw.Y) + c.C,
                    Y = (c.D * raw.X) + (c.E * raw.Y) + c.F,
                };
            }

            var (filtered, isInGap) = interpolator.Process(corrected);
            frames[i] = new Frame(raw, new PhysicalPoint(corrected.X, corrected.Y), filtered, isInGap);
        }
        return frames;
    }

    /// <summary>Identity filter — the reference point a candidate's attenuation is measured against.</summary>
    private sealed class PassThroughFilter : IGazeFilter
    {
        public PhysicalPoint Filter(GazePoint g) => new(g.X, g.Y);
        public void Reset() { }
    }
}
