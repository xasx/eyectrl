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
/// WHAT THE PINNED MODEL DOES NOT SEE — read this before quoting the number. It is a DISPLAY model
/// with no interaction state: every rule run is pinned from its onset through its last credited
/// sample, as if the widget were always on screen for the whole hold. The real app hides the ring
/// when an activation opens the action menu or the magnifier, and after such an overlay closes the
/// latched run never re-pins. So the pinned share is an UPPER BOUND, and the two
/// <see cref="PostActivation"/> treatments bound the other side: dropping the post-activation frames
/// (what the app effectively does while the overlay is up) and counting them as following (the worst
/// case). The frame set, the runs and the anchors all come from the committed
/// <see cref="FixationReplayGates"/> instrument, whose runs are byte-pinned to the engine's real
/// onsets by <c>ReplaySliceFidelityTests</c>; only the display rule is modelled here.
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
    /// How the model treats frames after the covering run's activation — the interaction the display
    /// model has no state for.
    /// </summary>
    public enum PostActivation
    {
        /// <summary>Pinned through the last credited sample. The UPPER BOUND of the real widget.</summary>
        Pinned,

        /// <summary>Drop post-activation frames, as the app does while the menu or magnifier covers the ring.</summary>
        Dropped,

        /// <summary>Worst case: follow gaze again for the rest of the run once activation has fired.</summary>
        Following,
    }

    /// <summary>
    /// The display models a measurement can be taken through: the pre-ticket-4 following ring, the
    /// ticket-4 pinned ring (pins from the run's onset), or ticket 7's pin at settle with its glide.
    /// </summary>
    /// <param name="PinnedRuns">
    /// The runs whose press holds the ring, or null for the "today" model that follows gaze everywhere.
    /// </param>
    /// <param name="PinAtSettle">
    /// Ticket 7's model: the ring follows gaze until the run SETTLES, then eases to the settle centroid
    /// over <paramref name="GlideMs"/> ms, then holds. A run that never settled never pins.
    /// </param>
    /// <param name="GlideMs">
    /// The glide's duration for the settle model. The glide frames are counted as FOLLOWING — the ring
    /// is deliberately moving across them, so they are not still, and the prediction stays conservative.
    /// </param>
    public sealed record Model(
        string Name,
        IReadOnlyList<FixationReplayGates.FixationRun>? PinnedRuns,
        bool PinAtSettle = false,
        int GlideMs = 0)
    {
        /// <summary>The pre-ticket-4 widget: the ring follows filtered gaze on every frame.</summary>
        public static Model Following { get; } = new("following (today)", null);

        /// <summary>Ticket 4's shipped widget: pinned at the run's onset anchor while a run covers the frame.</summary>
        public static Model Pinned(IReadOnlyList<FixationReplayGates.FixationRun> runs) =>
            new("pinned", runs);

        /// <summary>
        /// Ticket 7's widget: follows gaze, then glides to the run's settle centroid and holds there
        /// until the run ends. A run whose settle never came pins nowhere (M2's settle-plus-glide
        /// prediction, beside the onset-pinned model).
        /// </summary>
        public static Model PinnedAtSettle(
            IReadOnlyList<FixationReplayGates.FixationRun> runs, int glideMs) =>
            new("pinned at settle + glide", runs, PinAtSettle: true, GlideMs: glideMs);
    }

    /// <summary>
    /// Measures one display model on the frames of <paramref name="episodes"/>.
    /// </summary>
    public static Measurement Measure(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        IReadOnlyList<FixationReplayGates.HeldEpisode> episodes,
        Model model,
        double tolerancePx,
        int lagMs,
        PostActivation postActivation = PostActivation.Pinned)
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
                .Where(f => !f.IsInGap
                            && f.Raw.TimestampMicroseconds >= episode.StartUs
                            && f.Raw.TimestampMicroseconds <= episode.EndUs
                            && !(postActivation == PostActivation.Dropped
                                 && IsPostActivationCovered(f, model)))
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

                var here = RingAt(inEpisode[i], model, postActivation);
                var before = RingAt(inEpisode[predecessor], model, postActivation);
                considered++;
                if (here.DistanceTo(before) <= tolerancePx)
                    still++;
                if (IsPinned(inEpisode[i], model, postActivation))
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
        ReplayPipeline.Frame frame,
        Model model,
        PostActivation postActivation)
    {
        if (!IsPinned(frame, model, postActivation))
            return frame.Filtered;
        var run = RunFor(frame, model.PinnedRuns!)!;
        return model.PinAtSettle ? run.SettleAnchor ?? run.Anchor : run.Anchor;
    }

    /// <summary>
    /// Whether the ring is held on this frame. A covered frame is pinned except under
    /// <see cref="PostActivation.Following"/>, where the model gives the ring back to gaze after the
    /// run's activation. The settle model additionally follows gaze until the settle and through the
    /// glide — those frames move by design.
    /// </summary>
    private static bool IsPinned(
        ReplayPipeline.Frame frame,
        Model model,
        PostActivation postActivation)
    {
        if (model.PinnedRuns is null)
            return false;
        var run = RunFor(frame, model.PinnedRuns);
        if (run is null)
            return false;

        if (model.PinAtSettle)
        {
            if (run.SettleUs is not { } settleUs)
                return false;   // never settled: nothing to pin on
            if (frame.Raw.TimestampMicroseconds < settleUs + (model.GlideMs * 1000L))
                return false;   // pre-settle, or still gliding
        }

        return !(postActivation == PostActivation.Following && IsPostActivation(frame, run));
    }

    /// <summary>Whether the frame is covered AND sits after that run's activation sample.</summary>
    private static bool IsPostActivationCovered(
        ReplayPipeline.Frame frame, Model model)
        => model.PinnedRuns is not null && RunFor(frame, model.PinnedRuns) is { } run && IsPostActivation(frame, run);

    private static bool IsPostActivation(
        ReplayPipeline.Frame frame, FixationReplayGates.FixationRun run)
        => run.ActivationUs is { } activationUs && frame.Raw.TimestampMicroseconds > activationUs;

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
