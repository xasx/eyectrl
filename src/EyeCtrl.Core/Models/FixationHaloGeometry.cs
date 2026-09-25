namespace EyeCtrl.Core.Models;

/// <summary>
/// The geometry of the fixation halo, in device-independent pixels (DIP): one symmetric ring whose
/// radius grows from <c>R</c> to <c>2R</c> as the activation progress goes 0 to 1 (final synthesis,
/// step 1: "progress is one scalar on a symmetric halo of radius R–2R, with no moving arc head").
///
/// <para>
/// Pure and framework-free for the same reason <see cref="CursorRingStacks"/> is: EyeCtrl.Core cannot
/// reference WPF, and keeping the arithmetic here is what lets the units be pinned headlessly at
/// ScaleX 2.0. <see cref="EyeCtrl.Core.Settings.DwellSettings.FixationRadiusPixels"/> is a PHYSICAL
/// pixel value — the rule
/// never DPI-converts it — so the caller passes the live scale and this class divides it out. At the
/// user's R75 and ScaleX 2.0 that is 37.5 DIP at progress 0 and 75 DIP at progress 1.
/// </para>
///
/// <para>
/// The radius floors at <c>R</c>, never shrinking below the hold radius while the user holds: the
/// advocate's bias correction — a halo that contracts to a point converges on the measured point and
/// lures the eye off the target.
/// </para>
/// </summary>
public static class FixationHaloGeometry
{
    /// <summary>
    /// The halo radius in DIP at progress 0: <paramref name="fixationRadiusPixels"/> physical pixels
    /// divided by the live scale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the radius or the scale is not positive and finite.</exception>
    public static double InnerRadiusDip(double fixationRadiusPixels, double scaleX)
    {
        if (!double.IsFinite(fixationRadiusPixels) || fixationRadiusPixels <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(fixationRadiusPixels), fixationRadiusPixels, "Fixation radius must be positive and finite.");
        if (!double.IsFinite(scaleX) || scaleX <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), scaleX, "Scale must be positive and finite.");
        return fixationRadiusPixels / scaleX;
    }

    /// <summary>The halo radius in DIP at progress 1: <c>2R</c>, again divided by the live scale.</summary>
    public static double OuterRadiusDip(double fixationRadiusPixels, double scaleX)
        => 2 * InnerRadiusDip(fixationRadiusPixels, scaleX);

    /// <summary>
    /// The halo radius in DIP for <paramref name="progress"/>, clamped to 0–1: the inner radius plus
    /// the progress fraction of the inner radius (radius = R·(1 + progress) in physical pixels).
    /// </summary>
    public static double RadiusDip(double fixationRadiusPixels, double scaleX, double progress)
    {
        var inner = InnerRadiusDip(fixationRadiusPixels, scaleX);
        return inner * (1 + Math.Clamp(progress, 0.0, 1.0));
    }
}
