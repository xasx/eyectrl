namespace EyeCtrl.App.Services;

/// <summary>
/// Pure scroll physics: velocity interpolation, deadzone snapping, and integer-pixel
/// accumulation with remainder carry. Extracted from <see cref="ScrollWorker.OnTick"/>
/// so the math is unit-testable without a WPF DispatcherTimer.
/// </summary>
public static class ScrollPhysics
{
    /// <summary>
    /// Advances the scroll state by one tick.
    /// </summary>
    /// <param name="currentVx">Current X velocity (pixels/sec). Updated in place.</param>
    /// <param name="currentVy">Current Y velocity (pixels/sec). Updated in place.</param>
    /// <param name="targetVx">Target X velocity (pixels/sec).</param>
    /// <param name="targetVy">Target Y velocity (pixels/sec).</param>
    /// <param name="remainderX">Sub-pixel X remainder from prior ticks. Updated in place.</param>
    /// <param name="remainderY">Sub-pixel Y remainder from prior ticks. Updated in place.</param>
    /// <param name="dtMs">Tick duration in milliseconds.</param>
    /// <param name="intDeltaX">Integer X pixels to scroll this tick (output).</param>
    /// <param name="intDeltaY">Integer Y pixels to scroll this tick (output).</param>
    /// <param name="lerpFactor">Velocity interpolation factor per tick (0..1).</param>
    /// <param name="deadzone">Velocities below this magnitude snap to zero.</param>
    public static void Tick(
        ref double currentVx,
        ref double currentVy,
        double targetVx,
        double targetVy,
        ref double remainderX,
        ref double remainderY,
        int dtMs,
        out int intDeltaX,
        out int intDeltaY,
        double lerpFactor = 0.3,
        double deadzone = 0.01)
    {
        currentVx += (targetVx - currentVx) * lerpFactor;
        currentVy += (targetVy - currentVy) * lerpFactor;

        if (Math.Abs(targetVx) < deadzone && Math.Abs(currentVx) < deadzone) currentVx = 0;
        if (Math.Abs(targetVy) < deadzone && Math.Abs(currentVy) < deadzone) currentVy = 0;

        double deltaX = currentVx * dtMs / 1000.0 + remainderX;
        double deltaY = currentVy * dtMs / 1000.0 + remainderY;

        intDeltaX = (int)Math.Truncate(deltaX);
        intDeltaY = (int)Math.Truncate(deltaY);

        remainderX = deltaX - intDeltaX;
        remainderY = deltaY - intDeltaY;
    }
}
