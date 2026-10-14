namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Text;

/// <summary>
/// The A/B verdict logic, headless (review-01 and review-03). The labels are decided by
/// COMPARISON, not absolute thresholds: <see cref="LiveFlatBlack"/> is round 1's set (a lifted
/// capture that matches its baseline is UNREADABLE; then black / the flat fallback / live), and
/// <see cref="Material"/> is round 2's (black / flat neutral / flat tinted / varying). The pairwise
/// reading that turns several verdicts into a decision lives in the scripts; these labels never
/// decide it alone.
/// </summary>
internal static class BackdropClassification
{
    /// <summary>
    /// The round-1 verdict: UNREADABLE first when the lifted capture matches its baseline; then
    /// black (no material), flat (uniform), or live (spatially varying), with the numbers behind
    /// each.
    /// </summary>
    internal static string LiveFlatBlack(
        BackdropCapture.ScreenSample baseline, BackdropCapture.ScreenSample lifted, StringBuilder log)
    {
        var centre = lifted.Centre;
        var deltaMean = Math.Max(
            Math.Abs(centre.MeanR - baseline.Centre.MeanR),
            Math.Max(Math.Abs(centre.MeanG - baseline.Centre.MeanG), Math.Abs(centre.MeanB - baseline.Centre.MeanB)));
        var deltaStdDev = Math.Abs(centre.LumaStdDev - baseline.Centre.LumaStdDev);

        if (deltaMean <= 0.5 && deltaStdDev <= 0.5)
        {
            log.AppendLine(
                $"Comparison: the lifted capture matches the baseline (Δmean {deltaMean:0.0##}, Δstddev {deltaStdDev:0.0##}).");
            return "UNREADABLE (the affinity lift did not reach the capture; re-run)";
        }

        log.AppendLine(
            $"Comparison: mean moves from the baseline's ({baseline.Centre.MeanR:0.0}, {baseline.Centre.MeanG:0.0}, "
            + $"{baseline.Centre.MeanB:0.0}) to ({centre.MeanR:0.0}, {centre.MeanG:0.0}, {centre.MeanB:0.0}) "
            + $"(Δmean {deltaMean:0.0}); luma stddev {baseline.Centre.LumaStdDev:0.00} -> {centre.LumaStdDev:0.00}.");

        if (centre.BlackFraction >= 0.98)
            return "black (no material: the Mica path draws no darken fill, so nothing is behind it)";
        if (centre.LumaStdDev < 1.0)
            return "flat (uniform: Mica's inactive fallback or a solid fill — a fixed grey whatever lies behind)";
        return $"live (spatially varying, wallpaper-correlated: the mean moved {deltaMean:0.0} off the baseline's)";
    }

    /// <summary>
    /// The round-2 verdict: UNREADABLE first; then black (nothing reaches the surface), a flat grey
    /// (neutral = the expected never-activated fallback; tinted = suspect), or varying (a live
    /// material).
    /// </summary>
    internal static string Material(
        BackdropCapture.ScreenSample baseline, BackdropCapture.ScreenSample lifted, StringBuilder log)
    {
        var centre = lifted.Centre;
        var deltaMean = Math.Max(
            Math.Abs(centre.MeanR - baseline.Centre.MeanR),
            Math.Max(Math.Abs(centre.MeanG - baseline.Centre.MeanG), Math.Abs(centre.MeanB - baseline.Centre.MeanB)));
        var deltaStdDev = Math.Abs(centre.LumaStdDev - baseline.Centre.LumaStdDev);

        if (deltaMean <= 0.5 && deltaStdDev <= 0.5)
        {
            log.AppendLine(
                $"Comparison: the lifted capture matches the baseline (Δmean {deltaMean:0.0##}, Δstddev {deltaStdDev:0.0##}).");
            return "UNREADABLE (the affinity lift did not reach the capture; re-run)";
        }

        log.AppendLine(
            $"Comparison: mean moves from the baseline's ({baseline.Centre.MeanR:0.0}, {baseline.Centre.MeanG:0.0}, "
            + $"{baseline.Centre.MeanB:0.0}) to ({centre.MeanR:0.0}, {centre.MeanG:0.0}, {centre.MeanB:0.0}); "
            + $"luma stddev {baseline.Centre.LumaStdDev:0.00} -> {centre.LumaStdDev:0.00}.");

        if (centre.BlackFraction >= 0.98)
            return "black (nothing reaches the surface in this configuration; the script's reading says why)";

        var neutrality = Math.Max(
            Math.Abs(centre.MeanR - centre.MeanG),
            Math.Max(Math.Abs(centre.MeanR - centre.MeanB), Math.Abs(centre.MeanG - centre.MeanB)));
        if (centre.LumaStdDev < 1.0)
        {
            return neutrality <= 3.0
                ? "flat neutral grey (the composition-target fix worked; the never-activated surface's fallback — a design question if the material must be live)"
                : $"flat but tinted (suspect: the inactive fallback is neutral; channel spread {neutrality:0.0})";
        }

        return "varying (a live material in this configuration)";
    }
}
