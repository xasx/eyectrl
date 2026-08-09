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
}
