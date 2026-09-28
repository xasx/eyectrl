namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 7, part D: the 443 s gaze recording through the production detector, the settle, the offer
/// engine and the ring-still models, at the user's geometry — every metric the ticket lists, plus the
/// settle/radius sensitivity grid. Renders a markdown report; the env-gated test writes it to
/// <c>EYECTRL_REPLAY_REPORT_OUT</c>.
///
/// <para>
/// The engine drives through the same stream the product would give it: <c>Update</c> for every raw
/// sample, then the settle and end notifications as the production rule reports them. Everything the
/// engine needs is a caller input (the probe geometry, the chrome band, the dwell), so the sensitivity
/// grid is just the same walk at another constant.
/// </para>
/// </summary>
public static class ActivationReplayReport
{
    /// <summary>What the replay is run with. The defaults are the user's geometry and the shipped
    /// values, except the settle time and the hit radius, which this report exists to size.</summary>
    public sealed record Inputs(
        PhysicalRect ScreenBoundsPx,
        double Scale,
        double RingSizeDip,
        double FixationRadiusPx,
        int DwellMs,
        int SettleMs,
        double HitRadiusPx,
        int OutsidePauseBudgetMs,
        IReadOnlyList<PhysicalRect> ChromeZones,
        int GlideMs)
    {
        /// <summary>The user's geometry: 3840×2400 at ScaleX 2.0, ring 250, R75, dwell 700, the
        /// Top-docked toolbar band (120 DIP buttons + 2×12 DIP margins = 144 DIP = 288 px) as chrome,
        /// and the proposed settle 500 / hit radius 150.</summary>
        public static Inputs UserGeometry() => new(
            ScreenBoundsPx: new PhysicalRect(0, 0, 3840, 2400),
            Scale: 2.0,
            RingSizeDip: 250,
            FixationRadiusPx: 75,
            DwellMs: 700,
            SettleMs: FixationReplayGates.FixationSettleMs,
            HitRadiusPx: LayoutConstants.ActivationButtonHitRadiusPx,
            OutsidePauseBudgetMs: ActivationOfferEngine.DefaultOutsidePauseBudgetMs,
            ChromeZones: new[] { new PhysicalRect(0, 0, 3840, 288) },
            GlideMs: RingGlide.DurationMs);

        /// <summary>The same inputs at another settle time (the sensitivity grid).</summary>
        public Inputs WithSettle(int settleMs) => this with { SettleMs = settleMs };

        /// <summary>The same inputs at another hit radius (the sensitivity grid).</summary>
        public Inputs WithHitRadius(double hitRadiusPx) => this with { HitRadiusPx = hitRadiusPx };
    }

    /// <summary>Everything one walk produced.</summary>
    private sealed record Simulated(
        double Minutes,
        int FrameCount,
        int PopUps,
        long OnScreenUs,
        long TotalUs,
        List<(long Us, ActivationOffer Offer)> Offers,
        List<(long Us, long OfferId, bool InHit)> InHitFrames,
        List<Firing> Firings,
        List<Settle> Settles,
        int OneVisitCompletions,
        int MultiVisitCompletions)
    {
        public double PopUpsPerMinute => Minutes <= 0 ? 0 : PopUps / Minutes;
        public double CompletionsPerMinute => Minutes <= 0 ? 0 : Firings.Count / Minutes;
        public double OnScreenShare => TotalUs <= 0 ? 0 : (double)OnScreenUs / TotalUs;
    }

    private sealed record Firing(long Us, ActivationOffer Offer);

    private sealed record Settle(
        long Us,
        PhysicalPoint Point,
        SettleOutcome Outcome,
        PhysicalPoint? LiveButton,
        PhysicalPoint? LivePoint,
        bool InTopLeft,
        bool InTopRight);

    // The measured corner bias (tickets H1/N1): the midpoint of the probe ranges. Used only to bound
    // how the bias could move a settle's classification at a top corner.
    private const double TopLeftBiasPx = 115.0;
    private const double TopRightBiasPx = 80.0;

    /// <summary>Replays the recording and renders the report.</summary>
    public static string Build(GazeRecordingFile.Recording recording, string inputLabel, Inputs inputs)
    {
        var (frames, runs) = ReplayRule(recording, inputs);
        var baseline = Simulate(frames, runs, inputs);

        // The reset-on-excursion comparison: any outside sample resets the dwell.
        var resetInputs = inputs with { OutsidePauseBudgetMs = 0 };
        var resetSemantics = Simulate(frames, runs, resetInputs);

        var episodes150 = FixationReplayGates.ExtractHeldEpisodes(frames, FixationReplayGates.Dispersion150Px);
        var episodes200 = FixationReplayGates.ExtractHeldEpisodes(frames, FixationReplayGates.Dispersion200Px);
        var activatable = runs.Where(r => r.IsActivatable).ToList();
        var minutes = baseline.Minutes;

        var g1AtOnset = FixationReplayGates.MeasureAt(
            episodes150, activatable, minutes, FixationReplayGates.Dispersion150Px,
            FixationReplayGates.ServedDistancePx);
        var g1AtSettle = FixationReplayGates.MeasureAt(
            episodes150, activatable, minutes, FixationReplayGates.Dispersion150Px,
            FixationReplayGates.ServedDistancePx, atSettle: true);
        var g1AtOnset200 = FixationReplayGates.MeasureAt(
            episodes200, activatable, minutes, FixationReplayGates.Dispersion200Px,
            FixationReplayGates.ServedDistancePx);
        var g1AtSettle200 = FixationReplayGates.MeasureAt(
            episodes200, activatable, minutes, FixationReplayGates.Dispersion200Px,
            FixationReplayGates.ServedDistancePx, atSettle: true);

        // Ring stillness: ticket 4's shipped onset pin beside ticket 7's settle-plus-glide prediction.
        var ringStillOnset = RingStillness.Measure(
            frames, episodes150, RingStillness.Model.Pinned(runs), tolerancePx: 1.0, lagMs: 0);
        var ringStillSettle = RingStillness.Measure(
            frames, episodes150, RingStillness.Model.PinnedAtSettle(runs, inputs.GlideMs),
            tolerancePx: 1.0, lagMs: 0);

        var settleGrid = new List<(int SettleMs, Simulated Sim)>();
        foreach (int settleMs in new[] { 400, 500, 600 })
        {
            var (gridFrames, gridRuns) = ReplayRule(recording, inputs.WithSettle(settleMs));
            settleGrid.Add((settleMs, Simulate(gridFrames, gridRuns, inputs.WithSettle(settleMs))));
        }

        var radiusGrid = new List<(double RadiusPx, Simulated Sim, CornerStats Left, CornerStats Right)>();
        foreach (double radius in new[] { 125.0, 150.0, 175.0 })
        {
            var gridInputs = inputs.WithHitRadius(radius);
            var sim = Simulate(frames, runs, gridInputs);
            radiusGrid.Add((radius, sim, CornerStats.For(sim, inputs, topLeft: true), CornerStats.For(sim, inputs, topLeft: false)));
        }

        return Render(inputLabel, inputs, baseline, resetSemantics, g1AtOnset, g1AtSettle,
            g1AtOnset200, g1AtSettle200, ringStillOnset, ringStillSettle, settleGrid, radiusGrid, runs);
    }

    /// <summary>Runs the production rule (at the inputs' settle) over the recording's pipeline slice.</summary>
    private static (ReplayPipeline.Frame[] Frames, List<FixationReplayGates.FixationRun> Runs) ReplayRule(
        GazeRecordingFile.Recording recording, Inputs inputs)
    {
        FixationReplayGates.EnsureReplayable(recording);
        var candidate = new FixationReplayGates.Candidate(
            $"centroid rule, r{inputs.FixationRadiusPx:0}",
            () => new ProductionFixationRule(
                inputs.FixationRadiusPx,
                activationDwellMs: inputs.DwellMs,
                fixationSettleMs: inputs.SettleMs),
            () => new OneEuroFilter());
        var frames = ReplayPipeline.RunSynthetic(
            recording.Samples,
            new ReplayPipeline.Candidate(candidate.Name, candidate.CreateFilter, candidate.MaxGapMs),
            recording.Header.CalibrationTransform);
        return (frames, FixationReplayGates.DetectRuns(frames, candidate));
    }

    /// <summary>The walk: every frame through the engine, with the production rule's settle and end
    /// notifications at their samples, capturing every metric the report needs.</summary>
    private static Simulated Simulate(
        IReadOnlyList<ReplayPipeline.Frame> frames,
        IReadOnlyList<FixationReplayGates.FixationRun> runs,
        Inputs inputs)
    {
        var engine = new ActivationOfferEngine(
            inputs.ScreenBoundsPx, inputs.Scale, inputs.RingSizeDip, inputs.DwellMs,
            inputs.FixationRadiusPx, inputs.HitRadiusPx)
        {
            OutsidePauseBudgetMs = inputs.OutsidePauseBudgetMs,
        };
        engine.SetChromeZones(inputs.ChromeZones.ToArray());

        var settles = new Dictionary<long, FixationReplayGates.FixationRun>();
        foreach (var run in runs)
        {
            if (run.IsSettled)
                settles[run.SettleUs!.Value] = run;
        }
        var ends = new HashSet<long>(runs.Where(r => r.EndedByLeave).Select(r => r.EndUs));

        var offers = new List<(long Us, ActivationOffer Offer)>();
        var inHit = new List<(long Us, long OfferId, bool InHit)>();
        var firings = new List<Firing>();
        var settleRecords = new List<Settle>();
        long currentUs = 0;
        long? firedThisFrame = null;
        engine.OfferFired += offer =>
        {
            firings.Add(new Firing(currentUs, offer));
            firedThisFrame = offer.Id;
        };

        long firstUs = frames.Count > 0 ? frames[0].Raw.TimestampMicroseconds : 0;
        long previousUs = firstUs;
        long lastUs = firstUs;
        long onScreenUs = 0;
        int popUps = 0;
        long lastSeenOfferId = -1;

        // Every offer CREATION is a pop-up, wherever the engine made it: inside Update (the pending
        // re-offer) or inside NotifyFixationSettled (the ordinary path). A transition check around the
        // Update call alone misses the settle-created ones entirely.
        void CaptureOffer(long us)
        {
            var offer = engine.CurrentOffer;
            if (offer is null || offer.Id == lastSeenOfferId) return;
            lastSeenOfferId = offer.Id;
            popUps++;
            offers.Add((us, offer));
        }

        foreach (var frame in frames)
        {
            long ts = currentUs = frame.Raw.TimestampMicroseconds;
            long dt = ts - previousUs;
            previousUs = ts;
            lastUs = ts;

            var before = engine.CurrentOffer;
            engine.Update(frame.Filtered, ts, frame.Raw.IsValid);
            var after = engine.CurrentOffer;

            if (before is not null || after is not null)
                onScreenUs += Math.Max(0, dt);

            CaptureOffer(ts);
            if (after is not null)
                inHit.Add((ts, after.Id, frame.Filtered.DistanceTo(after.Button) <= after.HitRadiusPx));

            // A fire is a completion: the product resolves and then accepts it, so the replay does too
            // (immediately — the ≤500 ms resolve does not change the count). Without this the fired
            // lock would hold the offer for the rest of the stream, which is not a product behaviour.
            if (firedThisFrame is { } firedId)
            {
                engine.Accept(firedId);
                firedThisFrame = null;
            }

            if (settles.TryGetValue(ts, out var run))
            {
                var live = engine.CurrentOffer;
                var outcome = engine.NotifyFixationSettled(run.SettleAnchor!.Value, ts);
                settleRecords.Add(new Settle(
                    ts, run.SettleAnchor!.Value, outcome, live?.Button, live?.Point,
                    InCorner(frames, inputs, run.SettleAnchor!.Value, topLeft: true),
                    InCorner(frames, inputs, run.SettleAnchor!.Value, topLeft: false)));
                CaptureOffer(ts);
            }

            if (ends.Contains(ts))
                engine.NotifyFixationEnded(ts);
        }

        var (oneVisit, multiVisit) = CountVisits(firings, inHit);
        return new Simulated(
            (lastUs - firstUs) / 60_000_000.0,
            frames.Count,
            popUps,
            onScreenUs,
            Math.Max(1, lastUs - firstUs),
            offers,
            inHit,
            firings,
            settleRecords,
            oneVisit,
            multiVisit);
    }

    /// <summary>Splits the completions by how many separate in-hit visits made them: one continuous
    /// look at the button, or the same target reached by two or more glances (the N5 risk).</summary>
    private static (int OneVisit, int MultiVisit) CountVisits(
        IReadOnlyList<Firing> firings,
        IReadOnlyList<(long Us, long OfferId, bool InHit)> inHit)
    {
        int one = 0, multi = 0;
        foreach (var firing in firings)
        {
            int visits = 0;
            bool inVisit = false;
            foreach (var sample in inHit)
            {
                if (sample.OfferId != firing.Offer.Id) continue;
                if (sample.InHit && !inVisit) visits++;
                inVisit = sample.InHit;
            }
            if (visits <= 1) one++;
            else multi++;
        }
        return (one, multi);
    }

    /// <summary>Whether a point lies in the top-left or top-right quarter-height band.</summary>
    private static bool InCorner(
        IReadOnlyList<ReplayPipeline.Frame> frames, Inputs inputs, PhysicalPoint point, bool topLeft)
    {
        var screen = inputs.ScreenBoundsPx;
        if (point.Y > screen.Y + (screen.Height / 4))
            return false;
        return topLeft
            ? point.X < screen.X + (screen.Width / 4)
            : point.X > screen.X + (3 * screen.Width / 4);
    }

    /// <summary>
    /// The near-miss counts at a top corner, with the measured bias applied in both directions as
    /// bounds: the bias probe's sign is not part of its record. <see cref="All"/> is the same
    /// classification over every settle with a live offer, for context when a recording's corners carry
    /// few settles; it applies no bias (the measured values are corner offsets).
    /// </summary>
    private sealed record CornerStats(
        int Settles, int WithLiveOffer, int Hits, int NearMisses, int Outside,
        int? NearMissesBiasTowardCentre, int? NearMissesBiasAwayFromCentre)
    {
        public static CornerStats All(Simulated sim, Inputs inputs)
        {
            int settles = 0, withOffer = 0, hits = 0, near = 0, outside = 0;
            foreach (var settle in sim.Settles)
            {
                settles++;
                if (settle.LiveButton is not { } b || settle.LivePoint is not { } p) continue;
                withOffer++;
                switch (Classify(settle.Point, b, p, inputs))
                {
                    case CornerClass.Hit: hits++; break;
                    case CornerClass.NearMiss: near++; break;
                    default: outside++; break;
                }
            }
            return new CornerStats(settles, withOffer, hits, near, outside, null, null);
        }

        public static CornerStats For(Simulated sim, Inputs inputs, bool topLeft)
        {
            double bias = topLeft ? TopLeftBiasPx : TopRightBiasPx;
            var screen = inputs.ScreenBoundsPx;
            int settles = 0, withOffer = 0, hits = 0, near = 0, outside = 0, toward = 0, away = 0;
            foreach (var settle in sim.Settles)
            {
                bool inCorner = topLeft ? settle.InTopLeft : settle.InTopRight;
                if (!inCorner) continue;
                settles++;
                if (settle.LiveButton is not { } b || settle.LivePoint is not { } p) continue;
                withOffer++;
                switch (Classify(settle.Point, b, p, inputs))
                {
                    case CornerClass.Hit: hits++; break;
                    case CornerClass.NearMiss: near++; break;
                    default: outside++; break;
                }

                // The true aim is the reported point displaced by the bias along the point→centre line
                // (toward) or the opposite way (away).
                double centreX = screen.X + (screen.Width / 2);
                double centreY = screen.Y + (screen.Height / 2);
                double dx = centreX - settle.Point.X;
                double dy = centreY - settle.Point.Y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length <= 0) continue;
                var towardPoint = new PhysicalPoint(
                    settle.Point.X + (bias * dx / length), settle.Point.Y + (bias * dy / length));
                var awayPoint = new PhysicalPoint(
                    settle.Point.X - (bias * dx / length), settle.Point.Y - (bias * dy / length));
                if (Classify(towardPoint, b, p, inputs) == CornerClass.NearMiss) toward++;
                if (Classify(awayPoint, b, p, inputs) == CornerClass.NearMiss) away++;
            }
            return new CornerStats(settles, withOffer, hits, near, outside, toward, away);
        }

        private enum CornerClass { Hit, NearMiss, Outside }

        private static CornerClass Classify(
            PhysicalPoint point, PhysicalPoint button, PhysicalPoint offerPoint, Inputs inputs)
        {
            double toButton = point.DistanceTo(button);
            if (toButton <= inputs.HitRadiusPx) return CornerClass.Hit;
            if (toButton <= inputs.HitRadiusPx + inputs.FixationRadiusPx
                && toButton < point.DistanceTo(offerPoint))
                return CornerClass.NearMiss;
            return CornerClass.Outside;
        }
    }

    private static string Render(
        string inputLabel,
        Inputs inputs,
        Simulated sim,
        Simulated resetSemantics,
        FixationReplayGates.GateRow g1AtOnset,
        FixationReplayGates.GateRow g1AtSettle,
        FixationReplayGates.GateRow g1AtOnset200,
        FixationReplayGates.GateRow g1AtSettle200,
        RingStillness.Measurement ringStillOnset,
        RingStillness.Measurement ringStillSettle,
        IReadOnlyList<(int SettleMs, Simulated Sim)> settleGrid,
        IReadOnlyList<(double RadiusPx, Simulated Sim, CornerStats Left, CornerStats Right)> radiusGrid,
        IReadOnlyList<FixationReplayGates.FixationRun> runs)
    {
        var ci = CultureInfo.InvariantCulture;

        string Pct(double v) => (v * 100).ToString("0.0", ci) + " %";

        var pendingSettles = sim.Settles.Where(s => s.Outcome == SettleOutcome.Pending).ToList();
        var reOffers = Reoffers(sim, pendingSettles);
        var swallowedNeverReOffered = pendingSettles.Count - reOffers.Count(r => r.Satisfied);
        var noOfferSettles = sim.Settles.Count(s => s.Outcome == SettleOutcome.NoOffer);
        var topLeft = CornerStats.For(sim, inputs, topLeft: true);
        var topRight = CornerStats.For(sim, inputs, topLeft: false);
        var allSettles = CornerStats.All(sim, inputs);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: spec");
        sb.AppendLine("title: \"Replay report: settle time and hit radius, on the 443 s recording\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Replay report — the settle time and the activation hit radius");
        sb.AppendLine();
        sb.AppendLine($"**Input:** `{inputLabel}` — the frozen recording copy, replayed through the production");
        sb.AppendLine("centroid rule, the settle, the offer engine and the ring-still models.");
        sb.AppendLine();
        sb.AppendLine("**Geometry:** 3840×2400 at ScaleX 2.0, ring 250 DIP, R75 physical px, effective dwell 700 ms,");
        sb.AppendLine($"settle {inputs.SettleMs} ms, hit radius {inputs.HitRadiusPx.ToString("0", ci)} px, glide {inputs.GlideMs} ms,");
        sb.AppendLine("chrome = the Top-docked toolbar band (120 DIP buttons + 2×12 DIP margins = 144 DIP = 288 px).");
        sb.AppendLine();
        sb.AppendLine($"**Stream:** {sim.FrameCount.ToString(ci)} frames, {sim.Minutes.ToString("0.00", ci)} min.");
        sb.AppendLine();
        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| **G3′** button completions / min | {sim.CompletionsPerMinute.ToString("0.00", ci)} |");
        sb.AppendLine($"| G3′, one in-hit visit | {sim.OneVisitCompletions.ToString(ci)} |");
        sb.AppendLine($"| G3′, two or more in-hit visits | {sim.MultiVisitCompletions.ToString(ci)} |");
        sb.AppendLine($"| G3′ under reset-on-excursion | {resetSemantics.CompletionsPerMinute.ToString("0.00", ci)} / min |");
        sb.AppendLine($"| Pop-ups / min | {sim.PopUpsPerMinute.ToString("0.0", ci)} |");
        sb.AppendLine($"| Button on-screen share | {Pct(sim.OnScreenShare)} |");
        sb.AppendLine($"| Pins / min | {(sim.Minutes <= 0 ? 0 : sim.Settles.Count / sim.Minutes).ToString("0.0", ci)} |");
        sb.AppendLine($"| Swallowed settles / min | {(sim.Minutes <= 0 ? 0 : pendingSettles.Count / sim.Minutes).ToString("0.0", ci)} |");
        sb.AppendLine($"| Swallowed settles never re-offered (the dead end) | {swallowedNeverReOffered.ToString(ci)} |");
        sb.AppendLine($"| Settles with no offer (chrome band or no placeable side) | {noOfferSettles.ToString(ci)} |");
        sb.AppendLine($"| Re-offers that moved the button away from an attempt | {reOffers.Count(r => r.MovedAway).ToString(ci)} |");
        sb.AppendLine();
        sb.AppendLine("Settle classification (all " + sim.Settles.Count.ToString(ci) + " accepted settles):");
        sb.AppendLine();
        sb.AppendLine("| Outcome | Count | Per min |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (SettleOutcome outcome in Enum.GetValues<SettleOutcome>())
        {
            int count = sim.Settles.Count(s => s.Outcome == outcome);
            sb.AppendLine($"| {outcome} | {count.ToString(ci)} | {(sim.Minutes <= 0 ? 0 : count / sim.Minutes).ToString("0.0", ci)} |");
        }
        sb.AppendLine();
        sb.AppendLine("A `NoOffer` is the chrome rule working as decided: most of them are holds inside the");
        sb.AppendLine("Top-docked toolbar band, where the ring still pins and the offer is deliberately withheld");
        sb.AppendLine("(decision 8). `Extended` is a near miss, `Pending` a swallowed settle, `OnButton` a hold on");
        sb.AppendLine("the live button.");
        sb.AppendLine();
        sb.AppendLine("**The caveat:** the recording was made under the old UI, which never kept a button up");
        sb.AppendLine("after the fixation ended. Glances at a lingering button are therefore under-represented");
        sb.AppendLine("here, and **G3′ is a floor**, not an estimate. The live check decides.");
        sb.AppendLine();
        sb.AppendLine("## Near misses at the top corners");
        sb.AppendLine();
        sb.AppendLine("The measured bias (TL ~100–130 px, TR ~75–85 px) has no recorded sign, so both");
        sb.AppendLine("directions are reported as bounds: the settle point displaced toward the screen centre,");
        sb.AppendLine("and away from it.");
        sb.AppendLine();
        sb.AppendLine("| Region | settles | with a live offer | hit | near miss | outside | near miss, bias toward | near miss, bias away |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        AppendCorner(sb, "top-left quarter", topLeft);
        AppendCorner(sb, "top-right quarter", topRight);
        AppendCorner(sb, "all settles", allSettles);
        sb.AppendLine();
        sb.AppendLine("A near miss here is a settle in the live offer's no-move zone (hit + R around B) but");
        sb.AppendLine("outside the hit area and nearer B than P: it extends the offer and never moves the button");
        sb.AppendLine("(decision 10). The corner quarters are thin on this recording — its holds sit high and");
        sb.AppendLine("central — so read the all-settles row as the evidence and the corners as context.");
        sb.AppendLine();
        sb.AppendLine("## G1 at the settle centroid, beside the onset");
        sb.AppendLine();
        sb.AppendLine("| Anchor | D150 served | D200 served |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| onset | {g1AtOnset.ServedShare.ToString("P0", ci)} | {g1AtOnset200.ServedShare.ToString("P0", ci)} |");
        sb.AppendLine($"| settle | {g1AtSettle.ServedShare.ToString("P0", ci)} | {g1AtSettle200.ServedShare.ToString("P0", ci)} |");
        sb.AppendLine();
        sb.AppendLine("## Ring still share");
        sb.AppendLine();
        sb.AppendLine("1 px / previous-sample convention, on ticket 2's held episodes (ticket 4's measurement).");
        sb.AppendLine();
        sb.AppendLine("| Model | still | pinned | frames |");
        sb.AppendLine("| --- | --- | --- | --- |");
        sb.AppendLine($"| pinned at onset (ticket 4) | {ringStillOnset.StillShare.ToString("P1", ci)} | {ringStillOnset.PinnedShare.ToString("P1", ci)} | {ringStillOnset.ConsideredFrames.ToString(ci)} |");
        sb.AppendLine($"| pinned at settle + {inputs.GlideMs} ms glide (ticket 7) | {ringStillSettle.StillShare.ToString("P1", ci)} | {ringStillSettle.PinnedShare.ToString("P1", ci)} | {ringStillSettle.ConsideredFrames.ToString(ci)} |");
        sb.AppendLine();
        sb.AppendLine("## Sensitivity — settle time");
        sb.AppendLine();
        sb.AppendLine("| settle (ms) | pins / min | pop-ups / min | on-screen | G3′ / min | swallows / min |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var (settleMs, grid) in settleGrid)
        {
            double swallows = grid.Settles.Count(s => s.Outcome == SettleOutcome.Pending);
            sb.AppendLine($"| {settleMs.ToString(ci)} | {(grid.Minutes <= 0 ? 0 : grid.Settles.Count / grid.Minutes).ToString("0.0", ci)} | {grid.PopUpsPerMinute.ToString("0.0", ci)} | {Pct(grid.OnScreenShare)} | {grid.CompletionsPerMinute.ToString("0.00", ci)} | {(grid.Minutes <= 0 ? 0 : swallows / grid.Minutes).ToString("0.0", ci)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Sensitivity — hit radius");
        sb.AppendLine();
        sb.AppendLine("| hit radius (px) | G3′ / min | pop-ups / min | on-screen | TL near miss | TR near miss |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var (radiusPx, grid, left, right) in radiusGrid)
        {
            sb.AppendLine($"| {radiusPx.ToString("0", ci)} | {grid.CompletionsPerMinute.ToString("0.00", ci)} | {grid.PopUpsPerMinute.ToString("0.0", ci)} | {Pct(grid.OnScreenShare)} | {left.NearMisses.ToString(ci)} | {right.NearMisses.ToString(ci)} |");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"_(runs: {runs.Count.ToString(ci)}; settled: {runs.Count(r => r.IsSettled).ToString(ci)}.)_");
        sb.AppendLine();
        sb.AppendLine("## The user's decision (to be recorded)");
        sb.AppendLine();
        sb.AppendLine("This report is the gate for ticket 8. The proposal measured above is **settle 500 ms,");
        sb.AppendLine($"hit radius {inputs.HitRadiusPx.ToString("0", ci)} px**; the two sensitivity tables hold the range. The user confirms or");
        sb.AppendLine("changes them after the coordinator walks them through the numbers above, and the choice is");
        sb.AppendLine("recorded here.");
        sb.AppendLine();
        return sb.ToString();

        static void AppendCorner(StringBuilder sb, string name, CornerStats stats)
            => sb.AppendLine(
                $"| {name} | {N(stats.Settles)} | {N(stats.WithLiveOffer)} | {N(stats.Hits)} | {N(stats.NearMisses)} | {N(stats.Outside)} | {N(stats.NearMissesBiasTowardCentre)} | {N(stats.NearMissesBiasAwayFromCentre)} |");

        static string N(int? value) => value is { } v
            ? v.ToString(CultureInfo.InvariantCulture)
            : "n/a";
    }

    private sealed record Reoffer(Settle Pending, bool Satisfied, bool MovedAway);

    /// <summary>
    /// Classifies each swallowed settle: was an offer later made at its point (the re-offer), and was
    /// its point nearer the OLD button than the old P (which would mean the button walked away from an
    /// attempt — decision 10 forbids it, so this must stay 0).
    /// </summary>
    private static List<Reoffer> Reoffers(Simulated sim, IReadOnlyList<Settle> pendingSettles)
    {
        var result = new List<Reoffer>();
        foreach (var pending in pendingSettles)
        {
            bool satisfied = sim.Offers.Any(o => o.Us > pending.Us && o.Offer.Point == pending.Point);
            bool movedAway = pending.LiveButton is { } b && pending.LivePoint is { } p
                && pending.Point.DistanceTo(b) < pending.Point.DistanceTo(p);
            result.Add(new Reoffer(pending, satisfied, movedAway));
        }
        return result;
    }
}
