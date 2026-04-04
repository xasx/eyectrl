namespace EyeCtrl.Core.Models;

/// <summary>
/// Represents the physical dimensions of a screen in pixels.
/// Used for converting between physical pixels and normalized coordinates (0-65535)
/// for Win32 mouse input operations.
///
/// This type ensures screen dimensions are properly tracked when performing
/// coordinate transformations to/from the Win32 normalized coordinate space.
/// </summary>
/// <param name="Width">Screen width in physical pixels.</param>
/// <param name="Height">Screen height in physical pixels.</param>
public readonly record struct ScreenBounds(int Width, int Height);
