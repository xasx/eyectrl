namespace EyeCtrl.Platform;

/// <summary>
/// Platform-specific constants for Windows API interaction.
/// </summary>
public static class PlatformConstants
{
    /// <summary>
    /// Maximum value for Win32 absolute mouse input coordinates (65535).
    /// Win32 MOUSEEVENTF_ABSOLUTE uses a normalized coordinate system where
    /// (0,0) is the top-left corner and (65535, 65535) is the bottom-right corner.
    /// </summary>
    public const int Win32AbsoluteCoordinateMax = 65535;

    /// <summary>
    /// Standard mouse wheel delta value (120).
    /// Windows defines WHEEL_DELTA as 120, representing one notch of the mouse wheel.
    /// Scroll amounts are specified as multiples of this value.
    /// </summary>
    public const int MouseWheelDelta = 120;

    /// <summary>
    /// Standard Windows DPI at 100% scaling (96 DPI).
    /// Used as a reference point for DPI scaling calculations.
    /// </summary>
    public const int StandardDpiScale = 96;
}
