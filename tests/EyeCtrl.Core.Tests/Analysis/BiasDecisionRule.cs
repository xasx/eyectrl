namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;

/// <summary>
/// The pre-registered decision rule for a <see cref="BiasEstimator"/> estimate, kept next to the
/// estimator it judges so the two cannot drift apart.
/// <para>
/// <b>The rule, in full:</b>
/// <list type="number">
/// <item><b>Recalibrate in Tobii Dynavox Eye Tracking Settings</b> if |b| ≥ 60 px at ≥ 2 of the 5
/// targets, or ≥ 75 px (R75) at the centre target. Then run the probe again. 60 px is half the
/// toolbar's 120 px hit radius; 75 px is R, the fixation radius itself. The remedy is the device's
/// own calibration, never EyeCtrl's affine: the device is already Tobii-calibrated, and EyeCtrl's
/// affine is a residual layer that produced the 341 px failure and stays off.</item>
/// <item><b>Enable an EyeCtrl affine only</b> if the residual AFTER the Tobii recalibration is a
/// coherent linear pattern of magnitude ≥ R, and its leave-one-out residual beats identity. Not
/// implemented by this ticket — re-enabling the affine is explicitly out of scope — but pinned here
/// so the two halves of the rule live together and the second cannot be half-remembered later.</item>
/// </list>
/// </para>
/// <para>
/// <b>Only "No trigger" may ever claim the offset is small.</b> A rule that answers "No trigger"
/// whenever the surviving numbers are small is exactly the censoring this probe exists to remove: a
/// target whose bias differs from the majority is dropped by any consistency filter, and a
/// position-dependent residual (corner degradation, a scale error) is the commonest real shape. So
/// the verdict is three-way, and the two cautious answers are default:
/// <list type="bullet">
/// <item><b>Recalibrate</b> — when the measured targets alone already prove an arm. Coverage does
/// not matter here: more data could not un-prove it, and recalibrating is the safe direction.</item>
/// <item><b>No trigger</b> — only when every target that produced clusters was used AND at least 4 of
/// the 5 targets were measured. Anything less cannot rule out "≥ 60 px at 2 of 5".</item>
/// <item><b>Inconclusive</b> — everything else: too few targets measured, clusters that disagree with
/// the field, no clusters at all, or two comparably supported consistent solutions
/// (<see cref="BiasEstimator.Estimate.Ambiguous"/>). The reason names the numbers so a re-run is
/// informed.</item>
/// </list>
/// A quiet probe is not a clean bill either, which is why the outcome and the per-target cluster
/// sizes travel with the estimate: "Inconclusive" means the evidence did not support a verdict, not
/// that the bias is zero.
/// </para>
/// </summary>
public static class BiasDecisionRule
{
    /// <summary>Per-target magnitude that counts as evidence of bias, in physical pixels (half the 120 px toolbar hit radius).</summary>
    public const double PerTargetTriggerPx = 60;

    /// <summary>Centre-target magnitude that triggers on its own, in physical pixels — the shipped R75 fixation radius.</summary>
    public const double CentreTriggerPx = 75;

    /// <summary>Targets at or above <see cref="PerTargetTriggerPx"/> required for the per-target arm to fire.</summary>
    public const int MinTargetsForTrigger = 2;

    /// <summary>
    /// Measured targets required before a "No trigger" verdict is allowed. The probe visits five
    /// targets; with fewer than four, "≥ 60 px at ≥ 2 of 5" cannot be ruled out by the data.
    /// </summary>
    public const int MinTargetsForNoTrigger = 4;

    /// <summary>
    /// How far below a bar "No trigger" requires every measured magnitude to sit. A second place
    /// 30–55 px from the hold pulls a true 62 px down to 47–60 px; without a band the verdict flips to
    /// "No trigger" with nothing flagged. The Recalibrate thresholds do not move — the band only turns
    /// "not proven" into Inconclusive instead of a confident small answer. Ten is the fixup's value:
    /// it keeps the (45, 0) sweep at No trigger 200/200 (45 is 5 px clear of the 50 px bound) while
    /// catching the 45–60 px blended readings.
    /// </summary>
    public const double NoTriggerGuardBandPx = 10;

    /// <summary>
    /// A chosen cluster smaller than one third of that target's largest cluster did not win its target
    /// on evidence — it completed a field assignment because the real hold could not form one (a
    /// centre-only bias, degraded diagonal corners, a field steeper than the gradient cap). "No
    /// trigger" is never allowed from a pick like that; the target is named and the answer is
    /// Inconclusive.
    /// </summary>
    public const int MinorityPickDivisor = 3;

    /// <summary>The verdict. Only <see cref="NoTrigger"/> claims the offset is small.</summary>
    public enum BiasVerdict
    {
        /// <summary>The measured bias proves an arm; recalibrate in Tobii Dynavox, then probe again.</summary>
        Recalibrate,

        /// <summary>Measured, complete enough, and below both arms.</summary>
        NoTrigger,

        /// <summary>Not enough evidence for either verdict; the reason says what was missing.</summary>
        Inconclusive,
    }

    /// <summary>One measured target: its position and how far its chosen cluster sat from it.</summary>
    /// <param name="TargetId">The window's target id.</param>
    /// <param name="Target">Where the user was asked to look, in physical pixels.</param>
    /// <param name="MagnitudePx">|b| for this target.</param>
    public sealed record TargetMagnitude(int TargetId, PhysicalPoint Target, double MagnitudePx);

    /// <summary>The verdict and the numbers behind it.</summary>
    /// <param name="Verdict">Recalibrate / No trigger / Inconclusive.</param>
    /// <param name="Reason">A sentence fit to log or show the operator, naming the numbers that decided.</param>
    /// <param name="MeasuredTargets">Every target with a chosen cluster, in presentation order.</param>
    /// <param name="CentreMagnitudePx">The centre target's magnitude, when it has one.</param>
    public sealed record Decision(
        BiasVerdict Verdict,
        string Reason,
        IReadOnlyList<TargetMagnitude> MeasuredTargets,
        double? CentreMagnitudePx)
    {
        /// <summary>True only for <see cref="BiasVerdict.Recalibrate"/>.</summary>
        public bool ShouldRecalibrate => Verdict == BiasVerdict.Recalibrate;
    }

    /// <summary>
    /// Applies the rule to an estimate. Targets the estimator could not measure are absent from
    /// <see cref="Decision.MeasuredTargets"/> rather than counted as zero: "not measured" and
    /// "measured at 0 px" must not look alike in a decision about recalibration.
    /// </summary>
    /// <param name="estimate">The estimator's output.</param>
    /// <param name="screenSize">Display size in physical pixels, to locate the centre target.</param>
    /// <param name="applyMinorityGuard">
    /// Whether the minority-pick guard may veto "No trigger". True for the winner — the session's real
    /// answer — and FALSE when the estimator is judging an alternative field's verdict inside the
    /// rival search: a rival is a minority pick somewhere by construction, so applying the guard there
    /// makes every fragment rival read Inconclusive and flags a clean small bias as Ambiguous
    /// (reviewer A's R4-1: No trigger fell from 100/100/100 to 99/76/26 at σ 9/12/15). The guard
    /// exists to keep a wander from *answering* for the winner, not to judge hypotheticals.
    /// </param>
    public static Decision Evaluate(
        BiasEstimator.Estimate estimate, PhysicalPoint screenSize, bool applyMinorityGuard = true)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        var measured = new List<TargetMagnitude>();
        foreach (var probe in estimate.Probes)
            if (probe.DistancePx is { } distance)
                measured.Add(new TargetMagnitude(probe.TargetId, probe.Target, distance));

        var centre = estimate.Probes
            .OrderBy(p => DistanceSquared(p.Target, new PhysicalPoint(screenSize.X / 2, screenSize.Y / 2)))
            .FirstOrDefault();
        double? centreMagnitude = null;
        if (centre is not null)
        {
            foreach (var target in measured)
                if (target.TargetId == centre.TargetId)
                    centreMagnitude = target.MagnitudePx;
        }

        int overThreshold = measured.Count(t => t.MagnitudePx >= PerTargetTriggerPx);
        bool perTarget = overThreshold >= MinTargetsForTrigger;
        bool atCentre = centreMagnitude is { } cm && cm >= CentreTriggerPx;

        // 1. Ambiguity first: two comparably supported consistent solutions are not a decision, and
        //    picking one can point the wrong way — the reviewers' stream D's rival is a look-away.
        if (estimate.Ambiguous)
        {
            var rival = estimate.Rival;
            string rivalText = rival is { } r
                ? $"b≈({r.Bias.X.ToString("0.0", ci)}, {r.Bias.Y.ToString("0.0", ci)}) px from {r.Targets} targets ({r.Samples} samples)"
                : "another solution";
            return new Decision(
                BiasVerdict.Inconclusive,
                $"Inconclusive — two consistent solutions are comparably supported: "
                + $"b≈({estimate.Bias!.Value.X.ToString("0.0", ci)}, {estimate.Bias!.Value.Y.ToString("0.0", ci)}) px "
                + $"from {estimate.TargetsUsed} targets versus {rivalText}. Re-run the probe when rested; "
                + "if in doubt, recalibrate in Tobii Dynavox Eye Tracking Settings.",
                measured,
                centreMagnitude);
        }

        // 2. A proven arm wins regardless of coverage: more targets could not un-prove it.
        if (perTarget || atCentre)
        {
            var arms = new List<string>();
            if (perTarget)
                arms.Add($"|b| >= {PerTargetTriggerPx:0} px at {overThreshold} of {estimate.Probes.Count} targets");
            if (atCentre)
                arms.Add($"centre |b| = {centreMagnitude!.Value.ToString("0.0", ci)} px >= {CentreTriggerPx:0} px");
            return new Decision(
                BiasVerdict.Recalibrate,
                string.Join("; ", arms)
                + ". Recalibrate in Tobii Dynavox Eye Tracking Settings, then run the probe again.",
                measured,
                centreMagnitude);
        }

        // 3. "No trigger" is only allowed when the evidence makes it DECIDABLE, not merely plentiful:
        //    every clustered target used, at least four of the five measured, the centre actually
        //    measured, and no unmeasured target that could complete the 2-of-5 arm. A count alone says
        //    nothing — G1's centre (truly 90 px) was unmeasured, and G2 had one measured target at
        //    70 px with another unmeasured that could have been the second.
        bool everyClusteredTargetUsed = estimate.TargetsUsed == estimate.TargetsWithData;
        int unmeasured = estimate.Probes.Count - measured.Count;
        bool centreMeasured = centreMagnitude is not null;
        bool perTargetArmDecidable = overThreshold + unmeasured < MinTargetsForTrigger;

        // A pick that is a small minority of its target's evidence lost on samples and only completed
        // the assignment because the real hold could not form one; and "No trigger" must mean CLEARLY
        // small, not merely a hair under a bar a second place can pull it across.
        var minorityPicks = applyMinorityGuard
            ? estimate.Probes
                .Where(p => p.Chosen is { } chosen && p.Clusters.Count > 0
                    && chosen.SampleCount * MinorityPickDivisor < p.Clusters.Max(c => c.SampleCount))
                .Select(p => p.TargetId)
                .ToList()
            : new List<int>();
        // The centre clause "centre < 75 − δ" would never bind: every measured magnitude — the centre
        // included — is already required to be below the per-target bar less δ (50), which is stricter
        // than 65, so it is deliberately not restated here.
        bool clearlySmall = measured.All(m => m.MagnitudePx < PerTargetTriggerPx - NoTriggerGuardBandPx);

        if (everyClusteredTargetUsed && measured.Count >= MinTargetsForNoTrigger
            && centreMeasured && perTargetArmDecidable && minorityPicks.Count == 0 && clearlySmall)
        {
            return new Decision(
                BiasVerdict.NoTrigger,
                $"No trigger: the largest measured |b| is {measured.Max(t => t.MagnitudePx).ToString("0.0", ci)} px "
                + $"across {measured.Count} of {estimate.Probes.Count} targets; centre "
                + $"{centreMagnitude!.Value.ToString("0.0", ci)} px (bars {PerTargetTriggerPx:0} px at "
                + $"{MinTargetsForTrigger}, {CentreTriggerPx:0} px at centre; No trigger needs "
                + $"{NoTriggerGuardBandPx:0} px clear of them).",
                measured,
                centreMagnitude);
        }

        // 4. Everything else is Inconclusive, and the reason must say WHICH evidence is missing: no
        //    clusters at all; clusters that disagree with the field; too few targets measured; the
        //    centre unmeasured; a minority pick; an undecidable arm; or a magnitude too close to a bar.
        string why;
        if (estimate.TargetsWithData == 0)
        {
            why = "no target produced a usable cluster (no steady holds were found)";
        }
        else if (!everyClusteredTargetUsed)
        {
            var dropped = estimate.Probes
                .Where(p => p.Chosen is null && p.Clusters.Count > 0)
                .Select(p => $"target {p.TargetId} (~{p.LargestClusterDistancePx!.Value.ToString("0.0", ci)} px)")
                .ToList();
            why = $"{estimate.TargetsUsed} of {estimate.Probes.Count} targets fit a smooth offset field; "
                + string.Join(", ", dropped) + " had clusters that disagree with it";
        }
        else if (measured.Count < MinTargetsForNoTrigger)
        {
            why = $"only {measured.Count} of {estimate.Probes.Count} targets were measured "
                + $"(fewer than the {MinTargetsForNoTrigger} a No-trigger verdict needs)";
        }
        else if (!centreMeasured)
        {
            why = "the centre target was not measured, so the centre arm was never tested";
        }
        else if (minorityPicks.Count > 0)
        {
            why = $"target(s) {string.Join(", ", minorityPicks)} were decided by less than a "
                + $"{1.0 / MinorityPickDivisor:P0} minority of their samples — a brief wander completed "
                + "the field where the real hold did not fit; treat the readings as unproven";
        }
        else if (!perTargetArmDecidable)
        {
            why = $"{overThreshold} measured target(s) are at or above {PerTargetTriggerPx:0} px and "
                + $"{unmeasured} target(s) were not measured, so the "
                + $"{MinTargetsForTrigger}-of-{MinTargetsForTrigger} arm is not decidable";
        }
        else if (measured.Count(m => m.MagnitudePx >= PerTargetTriggerPx - NoTriggerGuardBandPx) == 1
            && overThreshold < MinTargetsForTrigger)
        {
            // ONE target at or above the band while the arm needs two: the honest reading, not
            // "within 10 px of the bars" (which said 90 px was near 60). Name the target and say what
            // to do if it persists.
            var worst = measured.OrderByDescending(m => m.MagnitudePx).First();
            why = $"target {worst.TargetId} reads {worst.MagnitudePx.ToString("0.0", ci)} px; one target "
                + $"alone cannot fire the {MinTargetsForTrigger}-of-{estimate.Probes.Count} arm ("
                + $"{PerTargetTriggerPx:0} px at {MinTargetsForTrigger} targets), but a second target "
                + $"near the {PerTargetTriggerPx:0} px bar cannot be ruled out. If a re-run repeats this "
                + "reading, recalibrating in Tobii Dynavox Eye Tracking Settings is reasonable";
        }
        else if (!clearlySmall)
        {
            why = $"the largest measured |b| is {measured.Max(t => t.MagnitudePx).ToString("0.0", ci)} px "
                + $"and the centre {centreMagnitude!.Value.ToString("0.0", ci)} px — within "
                + $"{NoTriggerGuardBandPx:0} px of the bars ({PerTargetTriggerPx:0} px at "
                + $"{MinTargetsForTrigger}, {CentreTriggerPx:0} px at centre), so 'small' is not clear";
        }
        else
        {
            why = $"the {MinTargetsForTrigger}-of-{MinTargetsForTrigger} arm is not decidable from "
                + $"{measured.Count} of {estimate.Probes.Count} targets";
        }

        return new Decision(
            BiasVerdict.Inconclusive,
            $"Inconclusive — {why}. Re-run the probe when rested; if in doubt, "
            + "recalibrate in Tobii Dynavox Eye Tracking Settings.",
            measured,
            centreMagnitude);
    }

    private static double DistanceSquared(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
