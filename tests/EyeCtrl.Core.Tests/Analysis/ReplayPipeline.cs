namespace EyeCtrl.Core.Tests.Analysis;

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
    /// <param name="Raw">The recorded sample, unmodified.</param>
    /// <param name="Filtered">The pipeline's output position for this sample (held during a gap).</param>
    /// <param name="IsInGap"><c>true</c> when the pipeline reported the position as held rather than measured.</param>
    public readonly record struct Frame(GazePoint Raw, PhysicalPoint Filtered, bool IsInGap);

    /// <summary>A named candidate pipeline: how to build its filter, and its gap budget.</summary>
    /// <param name="Name">Label used in reports, e.g. "baseline (OneEuro 5.0/0.5)".</param>
    /// <param name="CreateFilter">Factory for a FRESH filter — replay must never reuse filter state across runs.</param>
    /// <param name="MaxGapMs">The <c>MaxGapInterpolationMs</c> this candidate runs with.</param>
    public sealed record Candidate(string Name, Func<IGazeFilter> CreateFilter, int MaxGapMs = 150);

    /// <summary>The pipeline as shipped today: One-Euro at the current defaults, 150 ms gap budget.</summary>
    public static Candidate Baseline { get; } = new(
        "baseline (OneEuro minCutoff 5.0 / beta 0.5)",
        () => new OneEuroFilter(),
        MaxGapMs: 150);

    /// <summary>The no-filter reference: measures the raw stream through the same gap handling.</summary>
    public static Candidate Unfiltered { get; } = new(
        "unfiltered (gap handling only)",
        () => new PassThroughFilter(),
        MaxGapMs: 150);

    /// <summary>Replays the stream and returns one frame per input sample, in order.</summary>
    public static Frame[] Run(IReadOnlyList<GazePoint> samples, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(candidate);

        var interpolator = new GapInterpolator(candidate.CreateFilter(), candidate.MaxGapMs);
        var frames = new Frame[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var (filtered, isInGap) = interpolator.Process(samples[i]);
            frames[i] = new Frame(samples[i], filtered, isInGap);
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
