namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 7, part D: the 443 s gaze recording through the production detector, the settle, the offer
/// engine and the ring-still models — every metric the ticket lists, plus the settle / linger / radius
/// lever grid, at the user's live R and at ticket 3's default side by side (fixup 01, review B F1).
/// Renders a markdown report; the env-gated test writes it to <c>EYECTRL_REPLAY_REPORT_OUT</c>.
///
/// <para>
/// The engine drives through the same stream the product would give it: <c>Update</c> for every raw
/// sample, then the settle and end notifications as the production rule reports them, the end ON the
/// sample that ended the run (review B F9). Everything the engine needs is a caller input — the
/// geometry, the chrome band, the dwell, the settle, the hit radius, the linger — so every lever is
/// just the same walk at another constant.
/// </para>
/// </summary>
public static class ActivationReplayReport
{
    /// <summary>The user's live fixation radius in physical px: the settings file of 2026-09-28.
    /// Ticket 3's default and one-time migration is 75; the user set it back to 90 before the run.</summary>
    public const double UserFixationRadiusPx = 90;

    /// <summary>Ticket 3's default radius, shown beside the user's.</summary>
    public const double DefaultFixationRadiusPx = 75;

    /// <summary>The Top-docked toolbar band's thickness in DIP at the user's toolbar size 112:
    /// 112 button + 2×12 DIP margins + 2×4 DIP window padding = 144 DIP = 288 px.</summary>
    public const double ToolbarBandThicknessDip = 144;

    /// <summary>The toolbar strip's width in px, centred: the 7-button canvas (952 DIP at size 112)
    /// plus the indicator/resume-hint reserves and padding, ≈ 1225 DIP. Review B measured 2300–2600 px
    /// as immaterial to the headline; the shape — a centred strip, not full width — is what matters.</summary>
    public const double ToolbarBandWidthPx = 2450;

    /// <summary>The corner bias probe's midpoint (TL 100–130 px), used in the report's arithmetic on
    /// the probe, not on this recording.</summary>
    public const double CornerBiasPx = 115.0;

    /// <summary>What the replay is run with. Every lever the report moves is a field here.</summary>
    public sealed record Inputs(
        PhysicalRect ScreenBoundsPx,
        double Scale,
        double RingSizeDip,
        double FixationRadiusPx,
        int DwellMs,
        int SettleMs,
        double HitRadiusPx,
        int OutsidePauseBudgetMs,
        int OfferExpiryMs,
        IReadOnlyList<PhysicalRect> ChromeZones,
        int GlideMs)
    {
        /// <summary>The user's geometry: 3840×2400 at ScaleX 2.0, ring 250, dwell 700, the centred
        /// Top-docked toolbar strip as chrome, the proposed settle 500 / hit radius 150 / linger 1000.</summary>
        public static Inputs UserGeometry() => new(
            ScreenBoundsPx: new PhysicalRect(0, 0, 3840, 2400),
            Scale: 2.0,
            RingSizeDip: 250,
            FixationRadiusPx: UserFixationRadiusPx,
            DwellMs: 700,
            SettleMs: FixationReplayGates.FixationSettleMs,
            HitRadiusPx: LayoutConstants.ActivationButtonHitRadiusPx,
            OutsidePauseBudgetMs: ActivationOfferEngine.DefaultOutsidePauseBudgetMs,
            OfferExpiryMs: ActivationOfferEngine.DefaultOfferExpiryMs,
            ChromeZones: CentredToolbarBand(),
            GlideMs: RingGlide.DurationMs);

        /// <summary>The toolbar band as the real centred strip (not full width), in physical px.</summary>
        public static IReadOnlyList<PhysicalRect> CentredToolbarBand()
        {
            double x = (3840 - ToolbarBandWidthPx) / 2;
            return new[] { new PhysicalRect(x, 0, ToolbarBandWidthPx, ToolbarBandThicknessDip * 2) };
        }

        public Inputs WithSettle(int settleMs) => this with { SettleMs = settleMs };
        public Inputs WithHitRadius(double hitRadiusPx) => this with { HitRadiusPx = hitRadiusPx };
        public Inputs WithExpiry(int offerExpiryMs) => this with { OfferExpiryMs = offerExpiryMs };
        public Inputs WithFixationRadius(double fixationRadiusPx) =>
            this with { FixationRadiusPx = fixationRadiusPx };
    }

    private sealed record Firing(ActivationOffer Offer, int VisitsSinceLastReset);

    private sealed record Settle(
        long Us,
        PhysicalPoint Point,
        SettleOutcome Outcome,
        PhysicalPoint? LiveButton,
        long? LiveExpiresAtUs,
        bool InTopLeft,
        bool InTopRight);

    /// <summary>Everything one walk produced.</summary>
    private sealed record Simulated(
        double Minutes,
        int FrameCount,
        int PopUps,
        long HoldUs,
        long LingerUs,
        long ExtensionUs,
        long OnScreenUs,
        long TotalUs,
        long OutsideBandUs,
        long OutsideBandOnScreenUs,
        long InHitUs,
        int CompletionsResetSemantics,
        List<(long Us, ActivationOffer Offer)> Offers,
        List<Firing> Firings,
        List<Settle> Settles,
        HashSet<long> NearActivationOfferIds,
        int LookedAtOffers,
        int FirstLooksOver500Ms,
        int TopLeftSamples,
        int TopRightSamples,
        int TotalSamples)
    {
        public double PopUpsPerMinute => Minutes <= 0 ? 0 : PopUps / Minutes;
        public double CompletionsPerMinute => Minutes <= 0 ? 0 : Firings.Count / Minutes;
        public double OnScreenShare => TotalUs <= 0 ? 0 : (double)OnScreenUs / TotalUs;
        public double GazeOnButtonShare => OnScreenUs <= 0 ? 0 : (double)InHitUs / OnScreenUs;
        public double OutsideBandShare => OutsideBandUs <= 0 ? 0 : (double)OutsideBandOnScreenUs / OutsideBandUs;

        public (int OneVisit, int MultiVisit) SplitByVisits =>
            (Firings.Count(f => f.VisitsSinceLastReset <= 1),
             Firings.Count(f => f.VisitsSinceLastReset >= 2));
    }

    /// <summary>One geometry's full analysis: the walk, the ring models and G1.</summary>
    private sealed record Analysis(
        Inputs Inputs,
        Simulated Sim,
        List<FixationReplayGates.FixationRun> Runs,
        RingStillness.Measurement RingFollowing,
        RingStillness.Measurement RingOnsetPin,
        RingStillness.Measurement RingSettleNoGlide,
        RingStillness.Measurement RingSettleGlide,
        FixationReplayGates.GateRow G1OnsetInWindow,
        FixationReplayGates.GateRow G1SettleInWindow,
        FixationReplayGates.GateRow G1SettleOverSettledRuns);

    private sealed record LeverRow(string Value, Analysis AtUserR, Analysis AtDefaultR);

    private sealed record SwallowCounts(int HoldEndedFirst, int Stranded, int ReOffers);

    private sealed record CornerCount(int Settles, int WithLiveOffer, double SampleShare);

    /// <summary>Replays the recording and renders the report, at the user's R and ticket 3's default.</summary>
    public static string Build(GazeRecordingFile.Recording recording, string inputLabel, Inputs inputs)
    {
        var userR = Analyze(recording, inputs);
        var defaultR = Analyze(recording, inputs.WithFixationRadius(DefaultFixationRadiusPx));

        var lingerLevers = new List<LeverRow>();
        foreach (int expiryMs in new[] { 1000, 750, 500, 400 })
        {
            lingerLevers.Add(new LeverRow($"{expiryMs / 1000.0:0.##} s",
                Analyze(recording, inputs.WithExpiry(expiryMs)),
                Analyze(recording, inputs.WithExpiry(expiryMs).WithFixationRadius(DefaultFixationRadiusPx))));
        }

        var settleLevers = new List<LeverRow>();
        foreach (int settleMs in new[] { 400, 500, 600 })
        {
            settleLevers.Add(new LeverRow($"{settleMs} ms",
                Analyze(recording, inputs.WithSettle(settleMs)),
                Analyze(recording, inputs.WithSettle(settleMs).WithFixationRadius(DefaultFixationRadiusPx))));
        }

        var radiusLevers = new List<LeverRow>();
        foreach (double radiusPx in new[] { 125.0, 150.0, 175.0 })
        {
            radiusLevers.Add(new LeverRow($"{radiusPx:0} px",
                Analyze(recording, inputs.WithHitRadius(radiusPx)),
                Analyze(recording, inputs.WithHitRadius(radiusPx).WithFixationRadius(DefaultFixationRadiusPx))));
        }

        return Render(inputLabel, inputs, userR, defaultR, lingerLevers, settleLevers, radiusLevers);
    }

    /// <summary>Test seam: the headline numbers for one input set, without rendering the markdown.</summary>
    public static (double OnScreenShare, double PopUpsPerMinute, int Completions, int NearActivations)
        Headline(GazeRecordingFile.Recording recording, Inputs inputs)
    {
        var analysis = Analyze(recording, inputs);
        return (analysis.Sim.OnScreenShare, analysis.Sim.PopUpsPerMinute,
            analysis.Sim.Firings.Count, analysis.Sim.NearActivationOfferIds.Count);
    }

    private static Analysis Analyze(GazeRecordingFile.Recording recording, Inputs inputs)
    {
        var (frames, runs) = ReplayRule(recording, inputs);
        var sim = Simulate(frames, runs, inputs);
        var episodes = FixationReplayGates.ExtractHeldEpisodes(frames, FixationReplayGates.Dispersion150Px);
        var activatable = runs.Where(r => r.IsActivatable).ToList();
        var settled = runs.Where(r => r.IsSettled).ToList();
        double minutes = sim.Minutes;

        return new Analysis(
            inputs,
            sim,
            runs,
            RingStillness.Measure(frames, episodes, RingStillness.Model.Following, 1.0, 0),
            RingStillness.Measure(frames, episodes, RingStillness.Model.Pinned(runs), 1.0, 0),
            RingStillness.Measure(frames, episodes, RingStillness.Model.PinnedAtSettle(runs, glideMs: 0), 1.0, 0),
            RingStillness.Measure(frames, episodes, RingStillness.Model.PinnedAtSettle(runs, inputs.GlideMs), 1.0, 0),
            FixationReplayGates.MeasureAt(episodes, activatable, minutes,
                FixationReplayGates.Dispersion150Px, FixationReplayGates.ServedDistancePx),
            FixationReplayGates.MeasureAt(episodes, activatable, minutes,
                FixationReplayGates.Dispersion150Px, FixationReplayGates.ServedDistancePx, atSettle: true),
            FixationReplayGates.MeasureAt(episodes, settled, minutes,
                FixationReplayGates.Dispersion150Px, FixationReplayGates.ServedDistancePx, atSettle: true));
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
            OfferExpiryMs = inputs.OfferExpiryMs,
        };
        engine.SetChromeZones(inputs.ChromeZones.ToArray());

        var resetEngine = new ActivationOfferEngine(
            inputs.ScreenBoundsPx, inputs.Scale, inputs.RingSizeDip, inputs.DwellMs,
            inputs.FixationRadiusPx, inputs.HitRadiusPx)
        {
            OutsidePauseBudgetMs = 0,   // the comparison: reset after one outside sample, blinks included
            OfferExpiryMs = inputs.OfferExpiryMs,
        };
        resetEngine.SetChromeZones(inputs.ChromeZones.ToArray());
        var resetFiredOffers = new List<ActivationOffer>();
        long? resetFiredThisFrame = null;
        resetEngine.OfferFired += offer =>
        {
            resetFiredOffers.Add(offer);
            resetFiredThisFrame = offer.Id;
        };

        // The run each offer belongs to (by the run's settle time) and each run's end sample — together
        // they split the on-screen time into the hold, the linger and the extensions.
        var settledRuns = runs.Where(r => r.IsSettled).ToDictionary(r => r.SettleUs!.Value);
        var runEndBySettleUs = new Dictionary<long, long>();
        var endsAt = new HashSet<long>(runs.Where(r => r.EndedByLeave).Select(r => r.EndSampleOrEndUs));
        var offerRunSettleUs = new Dictionary<long, long>();
        long? currentRunSettleUs = null;

        var offers = new List<(long Us, ActivationOffer Offer)>();
        var inHit = new List<(long Us, long OfferId, bool InHit)>();
        var settleRecords = new List<Settle>();
        var nearActivation = new HashSet<long>();
        var firstHitUs = new Dictionary<long, long>();
        var runEndAtFirstHit = new Dictionary<long, long?>();
        int popUps = 0;
        long lastSeenOfferId = -1;
        long currentUs = 0;
        long? firedThisFrame = null;
        var firedOffers = new List<ActivationOffer>();
        engine.OfferFired += offer =>
        {
            _ = currentUs;
            firedOffers.Add(offer);
            firedThisFrame = offer.Id;
        };

        long firstUs = frames.Count > 0 ? frames[0].Raw.TimestampMicroseconds : 0;
        long previousUs = firstUs;
        long lastUs = firstUs;
        long holdUs = 0, lingerUs = 0, extensionUs = 0, onScreenUs = 0, inHitUs = 0;
        long outsideBandUs = 0, outsideBandOnScreenUs = 0;
        int topLeftSamples = 0, topRightSamples = 0;

        bool InBand(PhysicalPoint p)
        {
            for (int i = 0; i < inputs.ChromeZones.Count; i++)
            {
                if (inputs.ChromeZones[i].Contains(p.X, p.Y))
                    return true;
            }
            return false;
        }

        void CaptureOffer(long us)
        {
            var offer = engine.CurrentOffer;
            if (offer is null || offer.Id == lastSeenOfferId)
                return;
            lastSeenOfferId = offer.Id;
            popUps++;
            offers.Add((us, offer));
            if (currentRunSettleUs is { } runSettleUs)
                offerRunSettleUs[offer.Id] = runSettleUs;
        }

        // The on-screen bucket for one frame's dt, from the offer's run and the configured linger:
        // [creation, run end] is the hold, the next OfferExpiryMs is the linger, and everything after
        // is an extension bought by a look at the button or a near miss.
        void Classify(long offerId, long ts, long dt)
        {
            if (dt <= 0) return;
            long? runEndUs = offerRunSettleUs.TryGetValue(offerId, out var runSettle)
                             && runEndBySettleUs.TryGetValue(runSettle, out var end)
                ? end
                : null;
            if (runEndUs is not { } endUs || ts <= endUs)
                holdUs += dt;
            else if (ts <= endUs + (inputs.OfferExpiryMs * 1000L))
                lingerUs += dt;
            else
                extensionUs += dt;
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

            // The comparison run: the same stream with any single outside sample resetting the dwell
            // (blinks included), so the user can see what decision 9's pause buys.
            resetEngine.Update(frame.Filtered, ts, frame.Raw.IsValid);

            if (after is not null)
            {
                bool isInHit = frame.Filtered.DistanceTo(after.Button) <= after.HitRadiusPx;
                CaptureOffer(ts);
                inHit.Add((ts, after.Id, isInHit));
                onScreenUs += Math.Max(0, dt);
                Classify(after.Id, ts, Math.Max(0, dt));
                if (isInHit)
                    inHitUs += Math.Max(0, dt);
                if (engine.DwellProgress > 0.5)
                    nearActivation.Add(after.Id);
                if (isInHit && !firstHitUs.ContainsKey(after.Id))
                {
                    firstHitUs[after.Id] = ts;
                    runEndAtFirstHit[after.Id] =
                        offerRunSettleUs.TryGetValue(after.Id, out var runSettle)
                        && runEndBySettleUs.TryGetValue(runSettle, out var end)
                            ? end
                            : null;
                }
            }
            else if (before is not null)
            {
                // The offer died on this sample: its last interval was on screen, in the bucket the
                // state before the update was in.
                onScreenUs += Math.Max(0, dt);
                Classify(before.Id, ts, Math.Max(0, dt));
            }

            if (!InBand(frame.Filtered))
            {
                outsideBandUs += Math.Max(0, dt);
                if (after is not null || before is not null)
                    outsideBandOnScreenUs += Math.Max(0, dt);
                if (InCorner(inputs, frame.Filtered, topLeft: true)) topLeftSamples++;
                if (InCorner(inputs, frame.Filtered, topLeft: false)) topRightSamples++;
            }

            if (firedThisFrame is { } firedId)
            {
                engine.Accept(firedId);
                firedThisFrame = null;
            }
            if (resetFiredThisFrame is { } resetFiredId)
            {
                resetEngine.Accept(resetFiredId);
                resetFiredThisFrame = null;
            }

            if (settledRuns.TryGetValue(ts, out var run))
            {
                currentRunSettleUs = ts;
                var live = engine.CurrentOffer;
                var outcome = engine.NotifyFixationSettled(run.SettleAnchor!.Value, ts);
                settleRecords.Add(new Settle(
                    ts, run.SettleAnchor!.Value, outcome, live?.Button, live?.ExpiresAtUs,
                    InCorner(inputs, run.SettleAnchor!.Value, topLeft: true),
                    InCorner(inputs, run.SettleAnchor!.Value, topLeft: false)));
                CaptureOffer(ts);

                var resetLive = resetEngine.CurrentOffer;
                resetEngine.NotifyFixationSettled(run.SettleAnchor!.Value, ts);
                _ = resetLive;
            }

            if (endsAt.Contains(ts))
            {
                if (currentRunSettleUs is { } runSettle)
                    runEndBySettleUs[runSettle] = ts;
                engine.NotifyFixationEnded(ts);
                resetEngine.NotifyFixationEnded(ts);
                currentRunSettleUs = null;
            }
        }

        var firings = firedOffers
            .Select(offer => new Firing(offer, VisitsSinceLastReset(
                inHit.Where(f => f.OfferId == offer.Id).Select(f => (f.Us, f.InHit)),
                inputs.OutsidePauseBudgetMs)))
            .ToList();
        int firstLooksOver500 = firstHitUs.Count(kv =>
            runEndAtFirstHit[kv.Key] is { } end && kv.Value - end > 500_000);

        return new Simulated(
            (lastUs - firstUs) / 60_000_000.0,
            frames.Count,
            popUps,
            holdUs, lingerUs, extensionUs, onScreenUs,
            Math.Max(1, lastUs - firstUs),
            outsideBandUs, outsideBandOnScreenUs, inHitUs,
            resetFiredOffers.Count,
            offers, firings, settleRecords, nearActivation,
            firstHitUs.Count, firstLooksOver500,
            topLeftSamples, topRightSamples, frames.Count);
    }

    /// <summary>
    /// The in-hit visits that made a completion, counting only since the last reset (review B F8): an
    /// outside stretch longer than the pause budget erases the visits before it, exactly as it erases
    /// the dwell they credited.
    /// </summary>
    private static int VisitsSinceLastReset(
        IEnumerable<(long Us, bool InHit)> frames, int pauseBudgetMs)
    {
        int visits = 0;
        bool previousInHit = false;
        long? outsideSinceUs = null;
        long budgetUs = pauseBudgetMs * 1000L;
        foreach (var sample in frames)
        {
            if (sample.InHit)
            {
                if (!previousInHit) visits++;
                outsideSinceUs = null;
            }
            else
            {
                outsideSinceUs ??= sample.Us;
                if (sample.Us - outsideSinceUs > budgetUs)
                    visits = 0;
            }
            previousInHit = sample.InHit;
        }
        return visits;
    }

    /// <summary>Whether a point lies in the top-left or top-right quarter-height band (a 1/16 region).</summary>
    private static bool InCorner(Inputs inputs, PhysicalPoint point, bool topLeft)
    {
        var screen = inputs.ScreenBoundsPx;
        if (point.Y > screen.Y + (screen.Height / 4))
            return false;
        return topLeft
            ? point.X < screen.X + (screen.Width / 4)
            : point.X > screen.X + (3 * screen.Width / 4);
    }

    /// <summary>
    /// The exact 95 % Poisson rate range for <paramref name="count"/> events in
    /// <paramref name="minutes"/>: the λ interval where P(X ≥ count) = 0.025 (lower) and
    /// P(X ≥ count + 1) = 0.975 (upper), in per-minute units. Rare events deserve counts with ranges,
    /// not bare rates (review B F3).
    /// </summary>
    public static (double Lower, double Upper) Poisson95Range(int count, double minutes)
    {
        double lowerLambda = SolveSurvival(count, 0.025);
        double upperLambda = SolveSurvival(count + 1, 0.975);
        return (lowerLambda / minutes, upperLambda / minutes);

        static double SolveSurvival(int k, double target)
        {
            // P(X >= k) grows with λ; bisect for the λ where it meets the target.
            double lo = 0, hi = Math.Max(10, (4 * k) + 20);
            for (int i = 0; i < 200; i++)
            {
                double mid = (lo + hi) / 2;
                if (Survival(mid, k) < target) lo = mid;
                else hi = mid;
            }
            return (lo + hi) / 2;
        }

        static double Survival(double lambda, int k)
        {
            double term = Math.Exp(-lambda);
            double sum = term;
            for (int i = 1; i < k; i++)
            {
                term *= lambda / i;
                sum += term;
            }
            return 1 - sum;
        }
    }

    private static SwallowCounts Swallowed(Analysis analysis)
    {
        int holdEndedFirst = 0, stranded = 0, reOffers = 0;
        foreach (var settle in analysis.Sim.Settles.Where(s => s.Outcome == SettleOutcome.Pending))
        {
            // The pending's own run end: the first run end at or after the settle.
            long runEndUs = analysis.Runs
                .Where(r => r.EndedByLeave && r.EndSampleOrEndUs >= settle.Us)
                .Select(r => r.EndSampleOrEndUs)
                .OrderBy(us => us)
                .FirstOrDefault();
            bool reOffered = analysis.Sim.Offers.Any(o => o.Us > settle.Us && o.Offer.Point == settle.Point);
            if (reOffered)
                reOffers++;
            else if (settle.LiveExpiresAtUs is { } deadline && (runEndUs == 0 || runEndUs <= deadline))
                holdEndedFirst++;
            else
                stranded++;
        }
        return new SwallowCounts(holdEndedFirst, stranded, reOffers);
    }

    /// <summary>One compact line of the settle classification, per radius.</summary>
    private static string OutcomeSummary(Analysis analysis)
        => string.Join(" · ", Enum.GetValues<SettleOutcome>().Select(outcome =>
            $"{outcome} {analysis.Sim.Settles.Count(s => s.Outcome == outcome).ToString(CultureInfo.InvariantCulture)}"));

    private static CornerCount CornerCounts(Analysis analysis, bool topLeft)
    {
        int settles = 0, withOffer = 0;
        foreach (var settle in analysis.Sim.Settles)
        {
            bool inCorner = topLeft ? settle.InTopLeft : settle.InTopRight;
            if (!inCorner) continue;
            settles++;
            if (settle.LiveButton is not null) withOffer++;
        }
        int samples = topLeft ? analysis.Sim.TopLeftSamples : analysis.Sim.TopRightSamples;
        double share = analysis.Sim.TotalSamples == 0 ? 0 : (double)samples / analysis.Sim.TotalSamples;
        return new CornerCount(settles, withOffer, share);
    }

    private static string Render(
        string inputLabel,
        Inputs inputs,
        Analysis userR,
        Analysis defaultR,
        IReadOnlyList<LeverRow> lingerLevers,
        IReadOnlyList<LeverRow> settleLevers,
        IReadOnlyList<LeverRow> radiusLevers)
    {
        var ci = CultureInfo.InvariantCulture;
        string Pct(double v) => (v * 100).ToString("0.0", ci) + " %";
        string N(double v) => v.ToString("0.0", ci);
        string I(int v) => v.ToString(ci);
        string Rate(int count, Analysis a)
        {
            var range = Poisson95Range(count, a.Sim.Minutes);
            return $"{I(count)} in {a.Sim.Minutes.ToString("0.0", ci)} min ({N(count / a.Sim.Minutes)}/min; 95 % {range.Lower.ToString("0.000", ci)}–{range.Upper.ToString("0.00", ci)}/min)";
        }

        var userSplit = userR.Sim.SplitByVisits;
        var defaultSplit = defaultR.Sim.SplitByVisits;
        var userSwallow = Swallowed(userR);
        var defaultSwallow = Swallowed(defaultR);
        var tl = CornerCounts(userR, topLeft: true);
        var tr = CornerCounts(userR, topLeft: false);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("kind: spec");
        sb.AppendLine("title: \"Replay report: settle time, linger and hit radius, on the 443 s recording (R90 and R75)\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Replay report — the settle time, the linger and the activation hit radius");
        sb.AppendLine();
        sb.AppendLine($"**Input:** `{inputLabel}` — the frozen recording copy, replayed through the production");
        sb.AppendLine("centroid rule, the settle, the offer engine and the ring-still models.");
        sb.AppendLine();
        sb.AppendLine($"**Geometry:** 3840×2400 at ScaleX 2.0, ring 250 DIP, effective dwell {I(inputs.DwellMs)} ms,");
        sb.AppendLine($"settle {I(inputs.SettleMs)} ms, hit radius {I((int)inputs.HitRadiusPx)} px, linger {I(inputs.OfferExpiryMs)} ms, glide {inputs.GlideMs} ms.");
        sb.AppendLine($"**R = {I((int)UserFixationRadiusPx)} physical px, from the user's settings file of 2026-09-28** — the headline");
        sb.AppendLine($"runs at both that and ticket 3's default {I((int)DefaultFixationRadiusPx)}, which its migration set.");
        sb.AppendLine();
        sb.AppendLine($"**Chrome:** the Top-docked toolbar band, {I((int)ToolbarBandThicknessDip)} DIP = {I((int)(ToolbarBandThicknessDip * 2))} px thick");
        sb.AppendLine("(toolbar size 112 + 2×12 DIP margins + 2×4 DIP window padding) and centred, about");
        sb.AppendLine($"{I((int)ToolbarBandWidthPx)} px wide: the real strip, not the full width. The ring pins over it; no offer is made");
        sb.AppendLine("there (decision 8).");
        sb.AppendLine();
        sb.AppendLine($"**Stream:** {I(userR.Sim.FrameCount)} frames, {userR.Sim.Minutes.ToString("0.00", ci)} min.");
        sb.AppendLine();
        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine($"| Metric | R{I((int)UserFixationRadiusPx)} (the user's) | R{I((int)DefaultFixationRadiusPx)} (ticket 3's default) |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| **G3′** completions | {Rate(userR.Sim.Firings.Count, userR)} | {Rate(defaultR.Sim.Firings.Count, defaultR)} |");
        sb.AppendLine($"| G3′, one in-hit visit / two or more | {I(userSplit.OneVisit)} / {I(userSplit.MultiVisit)} | {I(defaultSplit.OneVisit)} / {I(defaultSplit.MultiVisit)} |");
        sb.AppendLine($"| G3′ under reset-on-excursion | {I(userR.Sim.CompletionsResetSemantics)} | {I(defaultR.Sim.CompletionsResetSemantics)} |");
        sb.AppendLine($"| Near-activations (dwell past half-way) | {I(userR.Sim.NearActivationOfferIds.Count)} | {I(defaultR.Sim.NearActivationOfferIds.Count)} |");
        sb.AppendLine($"| Pop-ups / min | {N(userR.Sim.PopUpsPerMinute)} | {N(defaultR.Sim.PopUpsPerMinute)} |");
        sb.AppendLine($"| Button on screen (anywhere) | {Pct(userR.Sim.OnScreenShare)} | {Pct(defaultR.Sim.OnScreenShare)} |");
        sb.AppendLine($"| — the hold after the settle | {Pct((double)userR.Sim.HoldUs / userR.Sim.TotalUs)} | {Pct((double)defaultR.Sim.HoldUs / defaultR.Sim.TotalUs)} |");
        sb.AppendLine($"| — the {I(inputs.OfferExpiryMs)} ms linger | {Pct((double)userR.Sim.LingerUs / userR.Sim.TotalUs)} | {Pct((double)defaultR.Sim.LingerUs / defaultR.Sim.TotalUs)} |");
        sb.AppendLine($"| — extensions from looks | {Pct((double)userR.Sim.ExtensionUs / userR.Sim.TotalUs)} | {Pct((double)defaultR.Sim.ExtensionUs / defaultR.Sim.TotalUs)} |");
        sb.AppendLine($"| Gaze on the button, share of visible time | {Pct(userR.Sim.GazeOnButtonShare)} | {Pct(defaultR.Sim.GazeOnButtonShare)} |");
        sb.AppendLine($"| Outside the toolbar band, a button is up | {Pct(userR.Sim.OutsideBandShare)} of that time | {Pct(defaultR.Sim.OutsideBandShare)} of that time |");
        sb.AppendLine($"| Buttons never looked at | {I(userR.Sim.PopUps - userR.Sim.LookedAtOffers)} of {I(userR.Sim.PopUps)} | {I(defaultR.Sim.PopUps - defaultR.Sim.LookedAtOffers)} of {I(defaultR.Sim.PopUps)} |");
        sb.AppendLine($"| First look > 500 ms after the hold ended | {I(userR.Sim.FirstLooksOver500Ms)} of {I(userR.Sim.LookedAtOffers)} looked-at | {I(defaultR.Sim.FirstLooksOver500Ms)} of {I(defaultR.Sim.LookedAtOffers)} looked-at |");
        sb.AppendLine($"| Pins / min | {N(userR.Runs.Count(r => r.IsSettled) / userR.Sim.Minutes)} | {N(defaultR.Runs.Count(r => r.IsSettled) / defaultR.Sim.Minutes)} |");
        sb.AppendLine($"| Settles with no offer (chrome/unplaceable) | {I(userR.Sim.Settles.Count(s => s.Outcome == SettleOutcome.NoOffer))} | {I(defaultR.Sim.Settles.Count(s => s.Outcome == SettleOutcome.NoOffer))} |");
        sb.AppendLine($"| Swallowed, hold ended first (no re-offer due) | {I(userSwallow.HoldEndedFirst)} | {I(defaultSwallow.HoldEndedFirst)} |");
        sb.AppendLine($"| Stranded (the hold outlasted the button) | {I(userSwallow.Stranded)} | {I(defaultSwallow.Stranded)} |");
        sb.AppendLine($"| Re-offers made | {I(userSwallow.ReOffers)} | {I(defaultSwallow.ReOffers)} |");
        sb.AppendLine("| Re-offers that moved the button away | 0 (by construction, decision 10) | 0 |");
        sb.AppendLine($"| Settle outcomes | {OutcomeSummary(userR)} | {OutcomeSummary(defaultR)} |");
        sb.AppendLine();
        sb.AppendLine("A \"no offer\" is the chrome rule working as decided: holds inside the toolbar band, where");
        sb.AppendLine("the ring still pins and the offer is deliberately withheld (decision 8).");
        sb.AppendLine();
        sb.AppendLine("**The caveat is two-sided.** The recording was made under the old UI: it never kept a button");
        sb.AppendLine("up after a fixation ended, so glances at a lingering button are under-represented (G3′ reads");
        sb.AppendLine("low); and it drew its decoy button at exactly the spot today's offer goes, so looks toward B");
        sb.AppendLine("are over-represented (G3′ reads high — the one completion looks like such a look: an up-right");
        sb.AppendLine("saccade to B, then a hold 75 px away, 392 ms after the hold ended). Read G3′ as \"rare on this");
        sb.AppendLine("recording\"; the live check decides.");
        sb.AppendLine();
        sb.AppendLine("## The top corners");
        sb.AppendLine();
        sb.AppendLine($"This recording has almost no corner evidence: the top-left quarter region (a 1/16 region)");
        sb.AppendLine($"holds {I(tl.Settles)} settles ({tl.SampleShare.ToString("P1", ci)} of samples) and the top-right {I(tr.Settles)} settles");
        sb.AppendLine($"({tr.SampleShare.ToString("P1", ci)} of samples), with {I(tl.WithLiveOffer)} and {I(tr.WithLiveOffer)} of them having a live offer. The corner");
        sb.AppendLine("near-miss columns are therefore gone: they were 0 by absence of data, not by result.");
        sb.AppendLine();
        sb.AppendLine($"The {I((int)inputs.HitRadiusPx)} px choice rests on the bias probe, not on this replay: an aim at a top-left");
        sb.AppendLine($"button lands about {I((int)CornerBiasPx)} px (probe range 100–130 px) from its centre, which leaves 20–50 px of");
        sb.AppendLine("margin inside 150 px (45–75 px at 175 px) against a hold wander of about 25 px per second.");
        sb.AppendLine("The live check at a top corner decides.");
        sb.AppendLine();
        sb.AppendLine("## G1 at the settle centroid, beside the onset");
        sb.AppendLine();
        sb.AppendLine("| Run set | Anchor | R90 served (D150) | R75 served (D150) |");
        sb.AppendLine("| --- | --- | --- | --- |");
        sb.AppendLine($"| activatable (age ≥ 1.0 s, the old gate) | onset | {userR.G1OnsetInWindow.ServedShare.ToString("P0", ci)} | {defaultR.G1OnsetInWindow.ServedShare.ToString("P0", ci)} |");
        sb.AppendLine($"| activatable | settle | {userR.G1SettleInWindow.ServedShare.ToString("P0", ci)} | {defaultR.G1SettleInWindow.ServedShare.ToString("P0", ci)} |");
        sb.AppendLine($"| **settled (age ≥ 0.5 s; every run that can be offered)** | settle | {userR.G1SettleOverSettledRuns.ServedShare.ToString("P0", ci)} | {defaultR.G1SettleOverSettledRuns.ServedShare.ToString("P0", ci)} |");
        sb.AppendLine();
        sb.AppendLine("Under the button flow every settled run gets an offer and can be clicked at P, so the settled");
        sb.AppendLine("row is the one to read (review B F7).");
        sb.AppendLine();
        sb.AppendLine("## Ring still share");
        sb.AppendLine();
        sb.AppendLine("1 px / previous-sample convention, on ticket 2's held episodes (ticket 4's measurement).");
        sb.AppendLine();
        sb.AppendLine("| Model | R90 | R75 |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| Following, before ticket 4 (\"almost never stands still\") | {userR.RingFollowing.StillShare.ToString("P1", ci)} | {defaultR.RingFollowing.StillShare.ToString("P1", ci)} |");
        sb.AppendLine($"| Onset pin, ticket 4 | {userR.RingOnsetPin.StillShare.ToString("P1", ci)} | {defaultR.RingOnsetPin.StillShare.ToString("P1", ci)} |");
        sb.AppendLine($"| Settle pin, no glide | {userR.RingSettleNoGlide.StillShare.ToString("P1", ci)} | {defaultR.RingSettleNoGlide.StillShare.ToString("P1", ci)} |");
        sb.AppendLine($"| Settle pin + {I(inputs.GlideMs)} ms glide (this ticket) | {userR.RingSettleGlide.StillShare.ToString("P1", ci)} | {defaultR.RingSettleGlide.StillShare.ToString("P1", ci)} |");
        sb.AppendLine();
        double laterPin = (userR.RingOnsetPin.StillShare - userR.RingSettleNoGlide.StillShare) * 100;
        double glidePart = (userR.RingSettleNoGlide.StillShare - userR.RingSettleGlide.StillShare) * 100;
        int shortRuns = userR.Runs.Count - userR.Runs.Count(r => r.IsSettled);
        string shortPerMinute = (shortRuns / userR.Sim.Minutes).ToString("0.0", ci);
        sb.AppendLine($"At R90 the drop from ticket 4's onset pin is {laterPin.ToString("0.0", ci)} points from pinning about 200 ms later and");
        sb.AppendLine($"{glidePart.ToString("0.0", ci)} points from counting the glide frames as movement (conservative by design). It is not a");
        sb.AppendLine($"regression against the original complaint — {userR.RingSettleGlide.StillShare.ToString("P1", ci)} against that ring's {userR.RingFollowing.StillShare.ToString("P1", ci)} —");
        sb.AppendLine("but a real reduction against ticket 4 during each long hold's first ~0.6 s: the cost of decision 1.");
        sb.AppendLine($"Not visible in the number: {I(shortRuns)} of {I(userR.Runs.Count)} fixations end before the settle ({shortPerMinute}/min) and now");
        sb.AppendLine($"never move the ring; the {N(userR.Runs.Count / userR.Sim.Minutes)} instant pin jumps per minute become");
        sb.AppendLine($"{N(userR.Runs.Count(r => r.IsSettled) / userR.Sim.Minutes)} glides per minute.");
        sb.AppendLine();
        sb.AppendLine("## Levers");
        sb.AppendLine();
        sb.AppendLine("Each row is the same recording with one constant moved, at both radii. The on-screen share is");
        sb.AppendLine("the time a button is visible somewhere; the linger is the biggest cheap lever (decision 3), the");
        sb.AppendLine("settle also moves when the ring pins and the time to activate, and the hit radius mostly moves");
        sb.AppendLine("completions.");
        sb.AppendLine();
        AppendLeverTable(sb, "Linger", lingerLevers);
        sb.AppendLine();
        AppendLeverTable(sb, "Settle time", settleLevers);
        sb.AppendLine();
        AppendLeverTable(sb, "Hit radius", radiusLevers);
        sb.AppendLine();
        sb.AppendLine($"At a 0.5 s linger, {I(userR.Sim.FirstLooksOver500Ms)} of the {I(userR.Sim.LookedAtOffers)} looks at a button on this recording would have");
        sb.AppendLine("found it gone; the one completion arrived 392 ms after the hold ended and would survive.");
        sb.AppendLine($"_(runs: {I(userR.Runs.Count)} at R90, {I(defaultR.Runs.Count)} at R75; settled: {I(userR.Runs.Count(r => r.IsSettled))} / {I(defaultR.Runs.Count(r => r.IsSettled))}.)_");
        sb.AppendLine();
        sb.AppendLine("## The user's decision (to be recorded)");
        sb.AppendLine();
        sb.AppendLine($"The proposal measured above is **settle {I(inputs.SettleMs)} ms, linger {I(inputs.OfferExpiryMs)} ms, hit radius");
        sb.AppendLine($"{I((int)inputs.HitRadiusPx)} px** at the user's R90; the Levers tables hold the ranges at both radii. The user");
        sb.AppendLine("confirms or changes them after the coordinator walks them through the numbers above, and the");
        sb.AppendLine("choice is recorded here.");
        sb.AppendLine();
        return sb.ToString();

        static void AppendLeverTable(StringBuilder sb, string title, IReadOnlyList<LeverRow> rows)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine($"**{title}**");
            sb.AppendLine();
            sb.AppendLine("| Value | On-screen R90 | On-screen R75 | Pop-ups/min R90 | Pop-ups/min R75 | G3′ R90 | G3′ R75 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var row in rows)
            {
                sb.AppendLine(
                    $"| {row.Value} | {(row.AtUserR.Sim.OnScreenShare * 100).ToString("0.0", ci)} % | {(row.AtDefaultR.Sim.OnScreenShare * 100).ToString("0.0", ci)} % "
                    + $"| {row.AtUserR.Sim.PopUpsPerMinute.ToString("0.0", ci)} | {row.AtDefaultR.Sim.PopUpsPerMinute.ToString("0.0", ci)} "
                    + $"| {row.AtUserR.Sim.Firings.Count.ToString(ci)} | {row.AtDefaultR.Sim.Firings.Count.ToString(ci)} |");
            }
        }
    }
}
