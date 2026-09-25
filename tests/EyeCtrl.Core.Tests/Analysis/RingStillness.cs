namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;

/// <summary>
/// The display half of ticket 4's acceptance, executable on any replay: on ticket 2's held-episode
/// frames, the share on which the drawn cursor ring does not move.
///
/// <para>
/// Two display models are measured through the same predicate. "Following" is the pre-ticket-4 widget:
/// the ring is at the pipeline's filtered point on every frame, so it moves with the eye's own
/// jitter. "Pinned" is the shipped widget: while a fixation run covers a frame the ring is at the
/// run's onset anchor (Core's running centroid at onset) and nothing can move it; outside a run it
/// follows as before.
/// </para>
///
/// <para>
/// A frame counts as STILL when the ring's position is within <c>tolerancePx</c> of its position at
/// least <c>lagMs</c> earlier — the debate's "same place 200 ms ago" convention, restricted to held
/// episodes exactly as the ticket asks. Only frames inside an episode that have such a predecessor
/// are considered, so short episodes contribute only their comparable tail.
/// </para>
///
/// <para>
/// This measures the DISPLAY rule, not the rule's own quality: the runs and episodes both come from
/// the committed gates (<see cref="FixationReplayGates"/>), and a pinned frame is by construction
/// uncovered by any other candidate. That separation is deliberate — the metric must not be able to
/// flatter the rule it is measured beside.
/// </para>
/// </summary>
public static class RingStillness
{
    /// <summary>One measurement: the still share, how much of it is the pin, and the frame count.</summary>
    /// <param name="StillShare">Share of considered frames on which the ring did not move within the tolerance.</param>
    /// <param name="PinnedShare">Share of considered frames on which the ring was held by a fixation run.</param>
    /// <param name="ConsideredFrames">Episode frames that had a predecessor at least one lag earlier.</param>
    public readonly record struct Measurement(double StillShare, double PinnedShare, int ConsideredFrames);

    /// <summary>
    /// The display models a measurement can be taken through: the pre-ticket-4 following ring, or the
    /// shipped pinned one.
    /// </summary>
    /// <param name="PinnedRuns">
    /// The runs whose press holds the ring, or null for the "today" model that follows gaze everywhere.
    /// </param>
    public sealed record Model(string Name, IReadOnlyList<FixationReplayGates.FixationRun>? PinnedRuns)
    {
        /// <summary>The pre-ticket-4 widget: the ring follows filtered gaze on every frame.</summary>
        public static Model Following { get; } = new("following (today)", null);

        /// <summary>The shipped widget: pinned at the run's onset anchor while a run covers the frame.</summary>
        public static Model Pinned(IReadOnlyList<FixationReplayGates.FixationRun> runs) =>
            new("pinned", runs);
    }

    /// <summary>
    /// Measures one display model on the frames of <paramref name="episodes"/>.
    /// </summary>
    public static Measurement Measure(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        IReadOnlyList<FixationReplayGates.HeldEpisode> episodes,
        Model model,
        double tolerancePx,
        int lagMs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(model);
        if (tolerancePx < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancePx), tolerancePx, "Tolerance must not be negative.");
        if (lagMs < 0)
            throw new ArgumentOutOfRangeException(nameof(lagMs), lagMs, "Lag must not be negative.");

        long lagUs = lagMs * 1000L;
        int considered = 0, still = 0, pinned = 0;

        foreach (var episode in episodes)
        {
            var inEpisode = frames
                .Where(f => !f.IsInGap && f.Raw.TimestampMicroseconds >= episode.StartUs
                                            && f.Raw.TimestampMicroseconds <= episode.EndUs)
                .ToList();

            int predecessor = -1;
            for (int i = 0; i < inEpisode.Count; i++)
            {
                long ts = inEpisode[i].Raw.TimestampMicroseconds;
                while (predecessor + 1 < i
                       && inEpisode[predecessor + 1].Raw.TimestampMicroseconds <= ts - lagUs)
                {
                    predecessor++;
                }

                if (predecessor < 0)
                    continue;

                var here = RingAt(inEpisode[i], model.PinnedRuns);
                var before = RingAt(inEpisode[predecessor], model.PinnedRuns);
                considered++;
                if (here.DistanceTo(before) <= tolerancePx)
                    still++;
                if (model.PinnedRuns is not null && IsPinned(inEpisode[i], model.PinnedRuns))
                    pinned++;
            }
        }

        return new Measurement(
            considered == 0 ? 0.0 : (double)still / considered,
            considered == 0 ? 0.0 : (double)pinned / considered,
            considered);
    }

    /// <summary>The ring position this frame draws under the model: the anchor while pinned, else gaze.</summary>
    private static PhysicalPoint RingAt(
        ReplayPipeline.Frame frame, IReadOnlyList<FixationReplayGates.FixationRun>? pinnedRuns)
    {
        var run = pinnedRuns is null ? null : RunFor(frame, pinnedRuns);
        return run is null ? frame.Filtered : run.Anchor;
    }

    /// <summary>
    /// Whether a run covers the frame. The pin lives from the onset sample through the last credited
    /// sample: on the leaving sample the rule has already ended the run, and the widget is hidden.
    /// </summary>
    private static bool IsPinned(
        ReplayPipeline.Frame frame, IReadOnlyList<FixationReplayGates.FixationRun>? pinnedRuns)
        => pinnedRuns is not null && RunFor(frame, pinnedRuns) is not null;

    private static FixationReplayGates.FixationRun? RunFor(
        ReplayPipeline.Frame frame, IReadOnlyList<FixationReplayGates.FixationRun> pinnedRuns)
    {
        long ts = frame.Raw.TimestampMicroseconds;
        foreach (var run in pinnedRuns)
        {
            if (run.OnsetUs <= ts && ts <= run.EndUs)
                return run;
        }
        return null;
    }
}
