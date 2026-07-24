namespace EyeCtrl.Core.Adaptation;

using System.Diagnostics;

/// <summary>
/// Pure, stateless scorers for the core struggle signals (design §13.2). Each maps an
/// <b>already time-windowed</b> slice of telemetry to a <see cref="SignalScore"/>. The
/// <c>StruggleMonitor</c> (plan 1b-③b) owns the evidence-window filtering, session resets, and
/// classification and simply calls these; keeping them pure makes them fully headless-testable over
/// synthetic sequences.
///
/// The window MUST be oldest-first with non-decreasing timestamps — guaranteed in production by
/// <c>BufferingTelemetrySink</c>'s monotonic guard (plan 1a-②/1a-④). The abandonment/accidental
/// early-<c>break</c> pruning and the poor-tracking span gate depend on it; DEBUG builds assert it via
/// <c>AssertOrdered</c> (compiled out entirely in release).
///
/// All numeric thresholds are optional parameters whose defaults are the §13.2 <b>starting values</b>,
/// tunable in review. The jitter/tracking px thresholds in particular are <b>uncalibrated guesses</b>
/// (jitter is measured on the FILTERED gaze stream — plan 1a-④) and are not authoritative.
/// </summary>
public static class StruggleSignals
{
    /// <summary>
    /// Abandonment / sustain-failure signal (design §13.2): the progress-weighted high-progress
    /// abandonment rate plus a retry term that folds in the "slow/effortful" signal (§6.2 — retries
    /// carry the same "make it easier" meaning; true time-to-activate is not derivable from
    /// <see cref="DwellOutcome"/>, which has no dwell-enter timestamp). Score =
    /// Σ(progress of high-progress abandons) ÷ attempts + retryBoost × (retries ÷ attempts), clamped
    /// to [0, 1]. "Attempts" is every outcome in the window (each activation or abandonment is one).
    /// A "retry" is ANY strictly-later same-context outcome within <paramref name="retryWindowMicroseconds"/>
    /// — activation OR abandonment: a re-attempt that also fails is still effort evidence. This matches
    /// §13.2's "re-engage" definition (clarified in 1b-③a review round 2 from the earlier "reactivate"
    /// wording, which read as activation-only); tunable.
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window, oldest first.</param>
    /// <param name="minAttempts">Confidence gate: minimum outcomes before the controller may act. Default 4.</param>
    /// <param name="highProgressThreshold">Minimum <see cref="DwellOutcome.ProgressAtEnd"/> for an abandon to count as "high-progress" (near-complete). Default 0.5.</param>
    /// <param name="retryBoost">Weight of the retry rate added to the score. Default 0.5.</param>
    /// <param name="retryWindowMicroseconds">Maximum gap for a later same-context outcome to count as a retry of an abandon. Default 3 000 000 (3 s).</param>
    /// <returns>The abandonment signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScoreAbandonment(
        IReadOnlyList<DwellOutcome> window,
        int minAttempts = 4,
        double highProgressThreshold = 0.5,
        double retryBoost = 0.5,
        long retryWindowMicroseconds = 3_000_000)
    {
        AssertOrdered(window, static o => o.TimestampMicroseconds);
        int attempts = window.Count;
        if (attempts == 0)
            return SignalScore.None;

        double highProgressSum = 0.0;
        int retries = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned)
                continue;

            if (o.ProgressAtEnd >= highProgressThreshold)
                highProgressSum += o.ProgressAtEnd;

            // Retry: a strictly-later same-context outcome within the retry window. The window is
            // time-ordered, so once the gap exceeds the retry window no later outcome can be closer.
            for (int j = i + 1; j < window.Count; j++)
            {
                long gap = window[j].TimestampMicroseconds - o.TimestampMicroseconds;
                if (gap > retryWindowMicroseconds)
                    break;
                if (gap > 0 && window[j].Context == o.Context)
                {
                    retries++;
                    break; // count each abandon at most once
                }
            }
        }

        double score = Math.Clamp(
            highProgressSum / attempts + retryBoost * ((double)retries / attempts),
            0.0, 1.0);
        double confidence = Math.Clamp((double)attempts / minAttempts, 0.0, 1.0);
        return new SignalScore(score, confidence, attempts >= minAttempts);
    }

    /// <summary>
    /// Accidental-activation signal (design §13.1/§13.2): the fraction of activations that were
    /// reversed. A reversal is either a <b>re-approach</b> — a strictly-later outcome in the SAME
    /// context whose target center lies within R of the activated target's center, within the
    /// re-approach window — or, in <see cref="DwellContext.Keyboard"/>, a key activation followed by a
    /// Backspace-key activation within the window (the corroborating signal, enabled only when
    /// <paramref name="backspaceTargetId"/> is supplied). This is the ONLY signal that drives a
    /// tightening (longer-dwell) response, quarantined and bounded (§8.2).
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window, oldest first.</param>
    /// <param name="minActivations">Confidence gate: minimum activations before the controller may act. Default 3.</param>
    /// <param name="reapproachWindowMicroseconds">Maximum gap for a following outcome to count as a reversal. Default 1 500 000 (1.5 s).</param>
    /// <param name="radiusMultiplier">Re-approach radius as a multiple of the activated target's radius. Default 2.0.</param>
    /// <param name="radiusFloorPx">Absolute floor (px) for the re-approach radius, covering small or rectangular (radius 0) targets. Default 40.</param>
    /// <param name="backspaceTargetId">Target id of the keyboard Backspace key; when non-null, enables the key→Backspace corroboration. Default null (disabled).</param>
    /// <returns>The accidental-activation signal score; <see cref="SignalScore.None"/> when the window has no activations.</returns>
    public static SignalScore ScoreAccidental(
        IReadOnlyList<DwellOutcome> window,
        int minActivations = 3,
        long reapproachWindowMicroseconds = 1_500_000,
        double radiusMultiplier = 2.0,
        double radiusFloorPx = 40.0,
        string? backspaceTargetId = null)
    {
        AssertOrdered(window, static o => o.TimestampMicroseconds);
        int activations = 0;
        for (int i = 0; i < window.Count; i++)
            if (window[i].Kind == OutcomeKind.Activated)
                activations++;
        if (activations == 0)
            return SignalScore.None;

        int reversals = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome a = window[i];
            if (a.Kind != OutcomeKind.Activated)
                continue;

            double radius = Math.Max(radiusMultiplier * a.TargetRadius, radiusFloorPx);
            double radiusSq = radius * radius;
            for (int j = i + 1; j < window.Count; j++)
            {
                long gap = window[j].TimestampMicroseconds - a.TimestampMicroseconds;
                if (gap > reapproachWindowMicroseconds)
                    break; // time-ordered → nothing later is closer
                if (gap <= 0)
                    continue;

                DwellOutcome n = window[j];
                // Intentional asymmetry (§13.1): re-approach accepts ANY follow-up kind ("activation
                // or ≥-threshold abandonment", §13.1:472-473), whereas Backspace corroboration requires
                // an actual Backspace *activation* (§13.1:474). Do not unify the Kind handling.
                bool reapproach = n.Context == a.Context
                    && n.TargetCenter.DistanceSquaredTo(a.TargetCenter) <= radiusSq;
                bool backspace = backspaceTargetId is not null
                    && a.Context == DwellContext.Keyboard
                    && n.Context == DwellContext.Keyboard
                    && n.Kind == OutcomeKind.Activated
                    && n.TargetId == backspaceTargetId;
                if (reapproach || backspace)
                {
                    reversals++;
                    break; // count each activation at most once
                }
            }
        }

        double score = Math.Clamp((double)reversals / activations, 0.0, 1.0);
        double confidence = Math.Clamp((double)activations / minActivations, 0.0, 1.0);
        return new SignalScore(score, confidence, activations >= minActivations);
    }

    /// <summary>
    /// Jitter / shakiness signal (design §13.2): a linear ramp of the mean fixation gaze-speed
    /// standard deviation across the window's summaries. Feeds the sustain-failure mode.
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window, oldest first.</param>
    /// <param name="minSummaries">Confidence gate: minimum summaries before the controller may act. Default 3.</param>
    /// <param name="lowJitterPxPerSec">Ramp start (px/s); at or below this the score is 0. Default 50 — UNCALIBRATED starting guess (jitter is on the filtered stream, plan 1a-④).</param>
    /// <param name="highJitterPxPerSec">Ramp end (px/s); at or above this the score is 1. Default 400 — UNCALIBRATED starting guess.</param>
    /// <returns>The jitter signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScoreJitter(
        IReadOnlyList<GazeQualitySummary> window,
        int minSummaries = 3,
        double lowJitterPxPerSec = 50.0,
        double highJitterPxPerSec = 400.0)
    {
        int count = window.Count;
        if (count == 0)
            return SignalScore.None;

        double sum = 0.0;
        for (int i = 0; i < window.Count; i++)
            sum += window[i].JitterStdDev;
        double mean = sum / count;

        double score = Ramp(mean, lowJitterPxPerSec, highJitterPxPerSec);
        double confidence = Math.Clamp((double)count / minSummaries, 0.0, 1.0);
        return new SignalScore(score, confidence, count >= minSummaries);
    }

    /// <summary>
    /// Poor-tracking signal (design §13.2/§6.3): a linear ramp of the sample-count-weighted mean
    /// invalid-sample fraction. Its gate is <b>time-based</b> (the summaries must span a minimum
    /// duration), NOT interaction-count-based, so tracking loss — which produces no dwell outcomes — is
    /// still detected promptly. Feeds the poor-tracking mode (never a tightening response).
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window, oldest first.</param>
    /// <param name="minSpanMicroseconds">Confidence gate: minimum time span the summaries must cover. Default 2 000 000 (2 s).</param>
    /// <param name="lowInvalidFraction">Ramp start; at or below this invalid fraction the score is 0. Default 0.1.</param>
    /// <param name="highInvalidFraction">Ramp end; at or above this invalid fraction the score is 1. Default 0.5.</param>
    /// <returns>The poor-tracking signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScorePoorTracking(
        IReadOnlyList<GazeQualitySummary> window,
        long minSpanMicroseconds = 2_000_000,
        double lowInvalidFraction = 0.1,
        double highInvalidFraction = 0.5)
    {
        AssertOrdered(window, static s => s.TimestampMicroseconds);
        int count = window.Count;
        if (count == 0)
            return SignalScore.None;

        double invalidSamples = 0.0;
        long totalSamples = 0;
        for (int i = 0; i < window.Count; i++)
        {
            GazeQualitySummary s = window[i];
            invalidSamples += s.InvalidFraction * s.SampleCount;
            totalSamples += s.SampleCount;
        }
        double meanInvalid = totalSamples > 0 ? invalidSamples / totalSamples : 0.0;

        long span = window[count - 1].TimestampMicroseconds - window[0].TimestampMicroseconds;
        double score = Ramp(meanInvalid, lowInvalidFraction, highInvalidFraction);
        double confidence = Math.Clamp((double)span / minSpanMicroseconds, 0.0, 1.0);
        return new SignalScore(score, confidence, span >= minSpanMicroseconds);
    }

    /// <summary>
    /// Aiming signal (design §13.2/§7.1): a linear ramp of the mean near-miss ratio
    /// <c>|NearMiss| ÷ TargetRadius</c> over circular-target abandons. A ratio &gt; 1 means the gaze was
    /// outside the target's hit radius at abandonment — poor aim → enlarge target geometry. Rectangular
    /// (radius 0) targets carry no usable radius, so they are excluded from both the mean and the gate
    /// count (rect-target aiming would need bounds-based near-miss — deferred). Feeds the aiming mode.
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window.</param>
    /// <param name="minAbandons">Confidence gate: minimum circular-target abandons before the controller may act. Default 4.</param>
    /// <param name="lowRatio">Ramp start; at or below this near-miss ratio the score is 0. Default 1.0 (gaze at the radius edge).</param>
    /// <param name="highRatio">Ramp end; at or above this ratio the score is 1. Default 2.5 — a tunable starting value.</param>
    /// <returns>The aiming signal score; <see cref="SignalScore.None"/> when there are no circular-target abandons.</returns>
    public static SignalScore ScoreAiming(
        IReadOnlyList<DwellOutcome> window,
        int minAbandons = 4,
        double lowRatio = 1.0,
        double highRatio = 2.5)
    {
        double ratioSum = 0.0;
        int count = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned || o.TargetRadius <= 0.0)
                continue; // only circular-target abandons have a usable near-miss ratio
            double magnitude = Math.Sqrt(o.NearMissX * o.NearMissX + o.NearMissY * o.NearMissY);
            ratioSum += magnitude / o.TargetRadius;
            count++;
        }

        if (count == 0)
            return SignalScore.None;

        double score = Ramp(ratioSum / count, lowRatio, highRatio);
        double confidence = Math.Clamp((double)count / minAbandons, 0.0, 1.0);
        return new SignalScore(score, confidence, count >= minAbandons);
    }

    /// <summary>
    /// Drift signal (design §13.1/§13.2/§6.2, C2): fires only when a near-miss bias is directionally
    /// coherent (within ±<paramref name="coherenceAngleDegrees"/>) across at least
    /// <paramref name="minCoherentContexts"/> distinct contexts, each with at least
    /// <paramref name="minAbandonsPerContext"/> abandons — the signature of a calibration slip. A bias
    /// confined to a single context is UI geometry, not drift, and never fires. Score = the coherent
    /// cluster's directional coherence × its normalized mean magnitude. The response is loosen tolerance
    /// + report; calibration is NEVER auto-touched (§8.5).
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window.</param>
    /// <param name="minAbandonsPerContext">Abandons a context needs to qualify as evidence. Default 5 (§13.1).</param>
    /// <param name="minCoherentContexts">Coherent contexts required to classify drift. Default 2 (§13.1).</param>
    /// <param name="coherenceAngleDegrees">Half-angle (degrees) of the coherence cone. Default 30 (§13.1).</param>
    /// <param name="magnitudeNormalizerPx">Mean bias magnitude (px) at which the magnitude term saturates to 1. Default 100 — an UNCALIBRATED starting guess.</param>
    /// <returns>The drift signal score; a zero, ungated score when fewer than <paramref name="minCoherentContexts"/> contexts qualify or none are coherent.</returns>
    public static SignalScore ScoreDrift(
        IReadOnlyList<DwellOutcome> window,
        int minAbandonsPerContext = 5,
        int minCoherentContexts = 2,
        double coherenceAngleDegrees = 30.0,
        double magnitudeNormalizerPx = 100.0)
    {
        // Per-context accumulation of NearMiss vectors over abandons (order-independent).
        var groups = new Dictionary<DwellContext, (double X, double Y, int N)>();
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned)
                continue;
            groups.TryGetValue(o.Context, out var g);
            groups[o.Context] = (g.X + o.NearMissX, g.Y + o.NearMissY, g.N + 1);
        }

        // Qualifying contexts: enough abandons AND a non-zero mean bias direction.
        var vectors = new List<(double X, double Y, double Mag)>();
        foreach (var g in groups.Values)
        {
            if (g.N < minAbandonsPerContext)
                continue;
            double mx = g.X / g.N, my = g.Y / g.N;
            double mag = Math.Sqrt(mx * mx + my * my);
            if (mag > 0.0)
                vectors.Add((mx, my, mag));
        }

        // Confidence = evidence volume (qualifying-context count); GatePassed = coherence achieved. These
        // are DECOUPLED here (unlike the 1b-③a scorers where the gate tracks confidence): a fully-confident
        // "not drift" — Confidence 1.0 with GatePassed false — is a valid, intended outcome (two biased
        // contexts that simply don't align). §13.2's gate is "the §13.1 gate itself" (coherence), not volume.
        double confidence = Math.Clamp((double)vectors.Count / minCoherentContexts, 0.0, 1.0);
        if (vectors.Count < minCoherentContexts)
            return new SignalScore(0.0, confidence, false);

        // Anchored "star" cluster (NOT a mutual clique): for each anchor, gather every vector within the
        // ±θ cone OF THE ANCHOR — two gathered vectors may themselves be > θ apart. This is intentional
        // (a calibration slip aligns most contexts one way; clique-finding is NP-hard), and the coherence
        // term |Σv| ÷ Σ|v| naturally penalizes a spread-out star. Coherence test: (a,b within θ) ⟺
        // dot(a,b) >= |a||b|·cos θ. Score = coherence (∈ (0,1], 1 = perfectly aligned) × normalized MEAN
        // magnitude — the mean cluster bias, NOT the total, so a bigger cluster does not inflate the score.
        // Ties in size break toward the higher score, so the result is independent of Dictionary order.
        double cosThreshold = Math.Cos(coherenceAngleDegrees * Math.PI / 180.0);
        int bestSize = 0;
        double bestScore = 0.0;
        for (int a = 0; a < vectors.Count; a++)
        {
            double rx = 0.0, ry = 0.0, sumMag = 0.0;
            int count = 0;
            for (int b = 0; b < vectors.Count; b++)
            {
                double dot = vectors[a].X * vectors[b].X + vectors[a].Y * vectors[b].Y;
                if (dot >= vectors[a].Mag * vectors[b].Mag * cosThreshold)
                {
                    rx += vectors[b].X;
                    ry += vectors[b].Y;
                    sumMag += vectors[b].Mag;
                    count++;
                }
            }

            double coherence = Math.Sqrt(rx * rx + ry * ry) / sumMag; // 1 = perfectly aligned
            double magNorm = Math.Clamp((sumMag / count) / magnitudeNormalizerPx, 0.0, 1.0);
            double clusterScore = Math.Clamp(coherence * magNorm, 0.0, 1.0);
            if (count > bestSize || (count == bestSize && clusterScore > bestScore))
            {
                bestSize = count;
                bestScore = clusterScore;
            }
        }

        bool gate = bestSize >= minCoherentContexts;
        return new SignalScore(gate ? bestScore : 0.0, confidence, gate);
    }

    /// <summary>
    /// Fatigue amplifier (design §13.2/§6.2): a pass-through of the mean windowed EyeCare fatigue score,
    /// gated on the opt-in <c>EyeCareSettings.FatigueMonitorEnabled</c> (passed as
    /// <paramref name="fatigueMonitorEnabled"/>). Unlike the other signals it maps to NO failure mode of
    /// its own — the <c>StruggleMonitor</c> (plan 1b-③c) uses it to AMPLIFY the aggregate / other modes.
    /// Reuses <c>FatigueMonitor.Score</c> (already computed on the gaze thread); it is not recomputed here.
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window.</param>
    /// <param name="fatigueMonitorEnabled">The <c>EyeCareSettings.FatigueMonitorEnabled</c> opt-in; when false the amplifier is inert.</param>
    /// <returns>A pass-through score of the mean <see cref="GazeQualitySummary.FatigueScore"/>; <see cref="SignalScore.None"/> when disabled or the window is empty.</returns>
    public static SignalScore ScoreFatigue(
        IReadOnlyList<GazeQualitySummary> window,
        bool fatigueMonitorEnabled)
    {
        if (!fatigueMonitorEnabled || window.Count == 0)
            return SignalScore.None;

        double sum = 0.0;
        for (int i = 0; i < window.Count; i++)
            sum += window[i].FatigueScore;

        return new SignalScore(sum / window.Count, 1.0, true);
    }

    // Linear ramp: 0 at/below low, 1 at/above high, linear between. Guards a degenerate low >= high
    // (returns a hard step at high) so a mis-passed threshold pair can't divide by zero.
    private static double Ramp(double value, double low, double high)
    {
        if (high <= low)
            return value >= high ? 1.0 : 0.0;
        return Math.Clamp((value - low) / (high - low), 0.0, 1.0);
    }

    // Dev-time tripwire for the oldest-first / non-decreasing precondition (see the class summary). The
    // early-break pruning (Abandonment/Accidental) and the poor-tracking span gate silently mis-score an
    // out-of-order window, so assert ordering in DEBUG. [Conditional("DEBUG")] removes the call — and its
    // lambda argument — entirely in release, so there is zero release / hot-path cost.
    [Conditional("DEBUG")]
    private static void AssertOrdered<T>(IReadOnlyList<T> window, Func<T, long> timestamp)
    {
        for (int i = 1; i < window.Count; i++)
            Debug.Assert(
                timestamp(window[i]) >= timestamp(window[i - 1]),
                "StruggleSignals requires an oldest-first, non-decreasing-timestamp window.");
    }
}
