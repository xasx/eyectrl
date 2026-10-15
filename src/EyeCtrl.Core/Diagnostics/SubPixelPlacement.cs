namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Models;

/// <summary>
/// Where a ring's top-left corner lands, in physical device pixels — the one placement rule the
/// sub-pixel visibility test varies between its two conditions.
/// <para>
/// The probe draws its own copy of the ring, and everything the eye could see — stroke, thickness,
/// look opacity, size, and the motion — is identical between A and B. A rounds the physical top-left
/// to whole device pixels; B places it exactly. Production does what A does: WPF lands
/// <c>Window.Left</c>/<c>Top</c> (written as <c>physical/scale − size/2</c>, in DIPs) on whole device
/// pixels.
/// </para>
/// <para>
/// Rounding the top-left and not the centre is the point: production rounds the value it writes to
/// <c>Left</c>/<c>Top</c>, which is the top-left, so a ring whose size is not a whole number of DIPs
/// does not land where a rounded centre would put it (review 01 V6).
/// </para>
/// </summary>
public static class SubPixelPlacement
{
    /// <summary>
    /// The physical top-left of a ring centred on <paramref name="centrePhys"/> with size
    /// <paramref name="sizeDip"/> DIPs at <paramref name="scale"/> device pixels per DIP:
    /// <c>centre − size·scale/2</c>, rounded half-away-from-zero to whole device pixels when
    /// <paramref name="snap"/> is set.
    /// <para>
    /// Condition A passes <c>snap: true</c> (whole-pixel, as production lands it); condition B passes
    /// <c>snap: false</c> (the exact sub-pixel position). A caller drawing at a centre adds
    /// <c>sizeDip·scale/2</c> back to the result.
    /// </para>
    /// </summary>
    /// <param name="centrePhys">The ring's exact centre, in physical device pixels.</param>
    /// <param name="sizeDip">The ring's diameter, in DIPs.</param>
    /// <param name="scale">The DIP→device-pixel scale in force on the display.</param>
    /// <param name="snap">Whether to round the top-left to whole device pixels (condition A).</param>
    /// <returns>The top-left corner, in physical device pixels.</returns>
    public static PhysicalPoint TopLeft(PhysicalPoint centrePhys, double sizeDip, double scale, bool snap)
    {
        double half = sizeDip * scale / 2.0;

        if (!snap)
            return new PhysicalPoint(centrePhys.X - half, centrePhys.Y - half);

        return new PhysicalPoint(
            Math.Round(centrePhys.X - half, MidpointRounding.AwayFromZero),
            Math.Round(centrePhys.Y - half, MidpointRounding.AwayFromZero));
    }
}
