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
    {
        if (stopZoneCenter.HasValue)
        {
            double sx = gaze.X - stopZoneCenter.Value.X;
            double sy = gaze.Y - stopZoneCenter.Value.Y;
            if (Math.Sqrt(sx * sx + sy * sy) <= stopZoneRadiusPx)
                return (0, 0);
        }

        double dx = gaze.X - anchor.X;
        double dy = anchor.Y - gaze.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance <= DeadZoneRadiusPixels)
            return (0, 0);

        double effective = distance - DeadZoneRadiusPixels;
        double magnitude = effective * DefaultPixelsToNotchesFactor * sensitivity;
        magnitude = Math.Min(magnitude, MaxVelocityNotchesPerSec);

        double vx = (dx / distance) * magnitude;
        double vy = (dy / distance) * magnitude;

        return (vx, vy);
    }
}
