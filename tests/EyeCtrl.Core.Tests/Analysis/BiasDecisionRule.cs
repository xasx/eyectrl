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
/// toolbar's 120 px hit radius, the point where a systematic offset starts eating completions; 75 px
/// is R, the fixation radius itself. The remedy is the device's own calibration, never EyeCtrl's
/// affine: the device is already Tobii-calibrated, and EyeCtrl's affine is a residual layer that
/// produced the 341 px failure and stays off.</item>
/// <item><b>Enable an EyeCtrl affine only</b> if the residual AFTER the Tobii recalibration is a
/// coherent linear pattern of magnitude ≥ R, and its leave-one-out residual beats identity. Not
/// implemented by this ticket — re-enabling the affine is explicitly out of scope — but pinned here
/// so the two halves of the rule live together and the second cannot be half-remembered later.</item>
/// </list>
/// A quiet probe is not a clean bill either, which is why the outcome and the per-target cluster
/// sizes travel with the estimate: "no estimate" means the evidence did not support one, not that the
/// bias is zero.
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

    /// <summary>One measured target: its position and how far its chosen cluster sat from it.</summary>
    /// <param name="TargetId">The window's target id.</param>
    /// <param name="Target">Where the user was asked to look, in physical pixels.</param>
    /// <param name="MagnitudePx">|b| for this target.</param>
    public sealed record TargetMagnitude(int TargetId, PhysicalPoint Target, double MagnitudePx);

    /// <summary>The verdict and the numbers behind it.</summary>
    /// <param name="ShouldRecalibrate">True when the Tobii-side recalibration is triggered.</param>
    /// <param name="Reason">A sentence fit to log or show the operator, naming the numbers that fired.</param>
    /// <param name="MeasuredTargets">Every target with a usable cluster, in presentation order.</param>
    /// <param name="CentreMagnitudePx">The centre target's magnitude, when it has one.</param>
    public sealed record Decision(
        bool ShouldRecalibrate,
        string Reason,
        IReadOnlyList<TargetMagnitude> MeasuredTargets,
        double? CentreMagnitudePx);

    /// <summary>
    /// Applies the rule to an estimate. Targets the estimator could not measure are absent from
    /// <see cref="Decision.MeasuredTargets"/> rather than counted as zero: "not measured" and
    /// "measured at 0 px" must not look alike in a decision about recalibration.
    /// </summary>
    /// <param name="estimate">The estimator's output.</param>
    /// <param name="screenSize">Display size in physical pixels, to locate the centre target.</param>
    public static Decision Evaluate(BiasEstimator.Estimate estimate, PhysicalPoint screenSize)
    {
        ArgumentNullException.ThrowIfNull(estimate);

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

        string reason;
        if (perTarget || atCentre)
        {
            var arms = new List<string>();
            if (perTarget)
                arms.Add($"|b| >= {PerTargetTriggerPx:0} px at {overThreshold} of {MinTargetsForTrigger}+ required targets");
            if (atCentre)
                arms.Add($"centre |b| = {centreMagnitude!.Value:0.0} px >= {CentreTriggerPx:0} px");
            reason = string.Join("; ", arms)
                + ". Recalibrate in Tobii Dynavox Eye Tracking Settings, then run the probe again.";
        }
        else if (measured.Count == 0)
        {
            reason = "No target produced a usable cluster; the probe has no evidence either way. Run it again when rested.";
        }
        else
        {
            reason = $"No trigger: {overThreshold} target(s) >= {PerTargetTriggerPx:0} px, centre "
                + (centreMagnitude is { } c ? $"{c:0.0} px" : "not measured")
                + $"; the largest is {measured.Max(t => t.MagnitudePx):0.0} px.";
        }

        return new Decision(perTarget || atCentre, reason, measured, centreMagnitude);
    }

    private static double DistanceSquared(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
