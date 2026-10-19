namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// Stateless pure-math engine that converts a gaze offset from an anchor point into a fractional
/// scroll velocity (notches/sec) for 2D gaze-joystick scrolling.
/// </summary>
public static class ScrollEngine
{
    /// <summary>Radius of the dead zone around the anchor in physical pixels. Gaze inside this zone produces zero velocity.</summary>
    public const double DeadZoneRadiusPixels = 30.0;
    /// <summary>Maximum scroll velocity in notches per second, clamped to avoid runaway scrolling.</summary>
    public const double MaxVelocityNotchesPerSec = 60.0;
    /// <summary>Conversion factor from effective pixels (beyond dead zone) to notches/sec at 1× sensitivity.</summary>
    public const double DefaultPixelsToNotchesFactor = 0.15;

    /// <summary>
    /// Computes the target scroll velocity from a gaze position relative to an anchor point.
    /// All coordinates are in physical pixels. Positive Y velocity means scroll up.
    /// </summary>
    /// <param name="gaze">Current gaze position in physical pixels.</param>
    /// <param name="anchor">The scroll anchor point (the fixation that opened the menu).</param>
    /// <param name="sensitivity">Sensitivity multiplier (0.5–2.0).</param>
    /// <param name="stopZoneCenter">Optional center of the stop-button zone. Gaze inside this zone forces zero velocity.</param>
    /// <param name="stopZoneRadiusPx">Radius of the stop zone in physical pixels.</param>
    /// <returns>A tuple (vx, vy) representing horizontal and vertical velocity in notches/sec.</returns>
    public static (double vx, double vy) ComputeVelocity(
        PhysicalPoint gaze,
        PhysicalPoint anchor,
        double sensitivity,
        PhysicalPoint? stopZoneCenter = null,
        double stopZoneRadiusPx = 50.0)
        => ComputeVelocity(
            gaze, anchor, sensitivity,
            deadZonePx: DeadZoneRadiusPixels, pxPerDip: 1.0,
            stopZoneCenter, stopZoneRadiusPx);

    /// <summary>
    /// The units overload (scroll start fix, F-S3): the caller passes the dead zone in PHYSICAL px
    /// and the gain's scale factor in DIP, so a 100-DIP effective offset gives the same notches/sec
    /// at every DPI. At <paramref name="pxPerDip"/> 1 with the 30-px dead zone this is identical to
    /// the legacy overload.
    /// </summary>
    /// <param name="gaze">Current gaze position in physical pixels.</param>
    /// <param name="anchor">The scroll anchor point, in physical pixels.</param>
    /// <param name="sensitivity">Sensitivity multiplier (0.5–2.0).</param>
    /// <param name="deadZonePx">Radius of the dead zone around the anchor, in PHYSICAL pixels.</param>
    /// <param name="pxPerDip">Physical pixels per DIP (the live scale): the proportional gain is
    /// <c>DefaultPixelsToNotchesFactor</c> per DIP beyond the dead zone.</param>
    /// <param name="stopZoneCenter">Optional center of the stop-button zone, in physical pixels.</param>
    /// <param name="stopZoneRadiusPx">Radius of the stop zone in physical pixels.</param>
    public static (double vx, double vy) ComputeVelocity(
        PhysicalPoint gaze,
        PhysicalPoint anchor,
        double sensitivity,
        double deadZonePx,
        double pxPerDip,
        PhysicalPoint? stopZoneCenter = null,
        double stopZoneRadiusPx = 0.0)
    {
        if (stopZoneCenter.HasValue &&
            gaze.DistanceTo(stopZoneCenter.Value) <= stopZoneRadiusPx)
        {
            return (0, 0);
        }

        double dx = gaze.X - anchor.X;
        double dy = anchor.Y - gaze.Y;
        double distance = gaze.DistanceTo(anchor);

        if (distance <= deadZonePx)
            return (0, 0);

        double effectiveDip = (distance - deadZonePx) / pxPerDip;
        double magnitude = effectiveDip * DefaultPixelsToNotchesFactor * sensitivity;
        magnitude = Math.Min(magnitude, MaxVelocityNotchesPerSec);

        double vx = (dx / distance) * magnitude;
        double vy = (dy / distance) * magnitude;

        return (vx, vy);
    }
}
