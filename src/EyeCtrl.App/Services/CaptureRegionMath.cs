namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// Pure screen-capture region math: center-and-radius to rectangle, clamped to screen bounds.
/// Extracted from <see cref="ScreenCaptureService.CaptureRegion"/> so the clamping logic
/// is testable without GDI+.
/// </summary>
public static class CaptureRegionMath
{
    /// <summary>
    /// Computes the capture rectangle for a square region centered on <paramref name="center"/>.
    /// </summary>
    /// <returns>The clamped (x, y, size), or null if the region is invalid.</returns>
    public static (int x, int y, int size)? Compute(int centerX, int centerY, int radius, int screenWidth, int screenHeight)
    {
        var captureX = centerX - radius;
        var captureY = centerY - radius;
        var captureSize = radius * 2;

        if (captureSize <= 0) return null;

        captureX = Math.Max(0, Math.Min(captureX, screenWidth - captureSize));
        captureY = Math.Max(0, Math.Min(captureY, screenHeight - captureSize));

        return (captureX, captureY, captureSize);
    }

    /// <summary>
    /// The centre of a capture rectangle, in physical pixels. Single derivation for both the
    /// rectangle a capture actually returned and one predicted before it runs.
    /// </summary>
    public static PhysicalPoint CenterOf((int x, int y, int size) region)
        => new(region.x + region.size / 2.0, region.y + region.size / 2.0);
}
