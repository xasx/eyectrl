namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>
/// The one place the calibration tolerance is defined.
/// <para>
/// <b>The band radius is a single number with three jobs</b>, and they must not be allowed to drift
/// apart: it decides whether gaze has ARRIVED at a target, whether a post-arrival sample COUNTS
/// towards that target's mean and σ, and it is what the UI DRAWS so the user can see the tolerance
/// they are being held to. Before this existed, the first was computed inline in
/// <c>CalibrationService.BeginTarget</c>, the second did not exist at all, and the third was a
/// hard-coded 40 px ellipse in <c>CalibrationWindow.xaml</c> — so the user aimed at a dot a twelfth
/// the area of the gate that actually judged them.
/// </para>
/// <para>
/// <b>Why it is anchored on the TARGET and not on where the user is looking.</b> A band around a
/// running centroid would measure stillness regardless of how far off the gaze sat, which is tempting
/// because raw gaze before calibration is systematically offset — that offset is the very thing being
/// measured. It was rejected because the tolerance then has no fixed place on screen and cannot be
/// drawn, and ticket 2's contract is that what is drawn is what is enforced. The cost is real and
/// deliberate: a user whose raw gaze sits further than this from a target collects nothing there and
/// the calibration fails. That is the intended outcome — it fails loudly instead of fitting a
/// transform to wander, which is what produced the 2026-09-12 calibration with ~900 px residuals.
/// </para>
/// </summary>
public static class CalibrationGeometry
{
    /// <summary>Floor for <see cref="BandRadiusPx"/> on small displays, in physical pixels.</summary>
    public const double MinimumBandRadiusPx = 120.0;

    /// <summary>Fraction of the display's shorter side used as the band radius above the floor.</summary>
    public const double BandRadiusScreenFraction = 0.10;

    /// <summary>
    /// The calibration band radius in physical pixels: how far from a target gaze may sit and still
    /// count as being on it.
    /// <para>
    /// Deliberately unchanged in value from the acceptance radius this replaces
    /// (<c>max(120, 0.10 × min(W,H))</c> — 120 px at 1080p, 240 px at 3840×2400). The 2026-09-12
    /// failure was not that this number was too generous; it was that nothing enforced it after
    /// arrival. Tightening it in the same change would have made a fix and a behaviour change
    /// indistinguishable, and would newly lock out users who can reach the target today.
    /// </para>
    /// <para>
    /// Scaled by the shorter side, not the diagonal: it is a radius on both axes, and a wide display
    /// does not make a user's vertical aim better.
    /// </para>
    /// </summary>
    /// <param name="screenSize">Display size in physical pixels.</param>
    public static double BandRadiusPx(PhysicalPoint screenSize) =>
        Math.Max(MinimumBandRadiusPx, BandRadiusScreenFraction * Math.Min(screenSize.X, screenSize.Y));
}
