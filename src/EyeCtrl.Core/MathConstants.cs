namespace EyeCtrl.Core;

/// <summary>
/// Mathematical constants for angle conversions and time conversions.
/// </summary>
public static class MathConstants
{
    /// <summary>
    /// Conversion factor from degrees to radians.
    /// To convert degrees to radians: radians = degrees * DegreesToRadians
    /// </summary>
    public const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>
    /// Number of degrees in a full circle (360°).
    /// Used for circular angle calculations and progress indicators.
    /// </summary>
    public const double FullCircleDegrees = 360.0;

    /// <summary>
    /// Number of degrees in a right angle (90°).
    /// Used for positioning items at cardinal directions.
    /// </summary>
    public const double RightAngleDegrees = 90.0;

    /// <summary>
    /// Number of degrees in a straight angle (180°).
    /// </summary>
    public const double StraightAngleDegrees = 180.0;

    /// <summary>
    /// Number of microseconds in one millisecond (1000).
    /// Used for converting between millisecond and microsecond timestamps.
    /// </summary>
    public const long MicrosecondsPerMillisecond = 1000L;
}
