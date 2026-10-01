namespace EyeCtrl.Core.Models;

/// <summary>
/// The geometry of the activation button's halo, in device-independent pixels (DIP): one symmetric
/// ring whose radius grows from half the button's hit radius at progress 0 to the full hit radius at
/// progress 1, with no moving arc head (final synthesis, step 1, retargeted in ticket 8).
///
/// <para>
/// The radii come from the button's HIT radius (<see cref="LayoutConstants.ActivationButtonHitRadiusPx"/>,
/// physical pixels): the outer radius never exceeds it, so no drawn feedback can lie outside the area
/// the dwell is actually counted within. Ticket 4's halo was sized from
/// <see cref="EyeCtrl.Core.Settings.DwellSettings.FixationRadiusPixels"/> and centred on the fixation
/// point; since ticket 8 the feedback is on the one action that fires — the button's dwell — and the
/// ring owns the hold's marking.
/// </para>
///
/// <para>
/// Pure and framework-free for the same reason <see cref="CursorRingStacks"/> is: EyeCtrl.Core cannot
/// reference WPF, and keeping the arithmetic here is what lets the units be pinned headlessly at
/// ScaleX 2.0. The radius is PHYSICAL pixels and the caller passes the live scale, which this class
/// divides out.
/// </para>
///
/// <para>
/// The radius floors at half the hit radius, never shrinking below it while the user holds: a halo
/// that contracts to a point converges on the measured point and lures the eye off the target.
/// </para>
/// </summary>
public static class ActivationButtonHaloGeometry
{
    /// <summary>
    /// The halo radius in DIP at progress 0: half of <paramref name="hitRadiusPixels"/> divided by the
    /// live scale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the radius or the scale is not positive and finite.</exception>
    public static double InnerRadiusDip(double hitRadiusPixels, double scaleX)
    {
        if (!double.IsFinite(hitRadiusPixels) || hitRadiusPixels <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(hitRadiusPixels), hitRadiusPixels, "Hit radius must be positive and finite.");
        if (!double.IsFinite(scaleX) || scaleX <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), scaleX, "Scale must be positive and finite.");
        return hitRadiusPixels / (2 * scaleX);
    }

    /// <summary>The halo radius in DIP at progress 1: the full hit radius, divided by the live scale.</summary>
    public static double OuterRadiusDip(double hitRadiusPixels, double scaleX)
        => 2 * InnerRadiusDip(hitRadiusPixels, scaleX);

    /// <summary>
    /// The halo radius in DIP for <paramref name="progress"/>, clamped to 0–1: the inner radius plus
    /// the progress fraction of the inner radius (radius = hit·(1 + progress)/2 in physical pixels).
    /// </summary>
    public static double RadiusDip(double hitRadiusPixels, double scaleX, double progress)
    {
        var inner = InnerRadiusDip(hitRadiusPixels, scaleX);
        return inner * (1 + Math.Clamp(progress, 0.0, 1.0));
    }
}
