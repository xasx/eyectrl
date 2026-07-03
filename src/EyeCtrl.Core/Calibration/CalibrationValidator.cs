namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>
/// Validates calibration transforms before they are applied or persisted.
/// A degenerate calibration (fit to near-constant or contradictory gaze samples) can produce
/// a finite — and therefore un-caught by <see cref="AffineTransform.Fit"/>'s non-finite/singular
/// guards — but wildly out-of-range affine transform that maps every gaze point far off-screen,
/// silently breaking all gaze interaction. These checks reject such transforms at both fit-time
/// (<see cref="CalibrationService.CompleteSession"/>) and load-time (<see cref="CalibrationService.Load"/>).
/// </summary>
public static class CalibrationValidator
{
    /// <summary>
    /// A usable transform must map the screen's own reference points to within this multiple of
    /// the screen size. Generous enough to allow real calibration corrections, tight enough to
    /// reject degenerate fits that fling gaze far off-screen.
    /// </summary>
    public const double MaxOffScreenFactor = 1.5;

    /// <summary>
    /// The largest post-transform residual must stay within this fraction of the screen diagonal
    /// for a calibration to be accepted. Generous, so only clearly-bad fits are rejected.
    /// </summary>
    public const double MaxResidualScreenFraction = 0.20;

    /// <summary>
    /// A raw gaze sample may sit at most this multiple of the screen size beyond an edge before it
    /// is treated as implausible (sensor noise can push gaze slightly off-screen; 50000px cannot).
    /// </summary>
    public const double MaxSampleOffScreenFactor = 0.5;

    /// <summary>
    /// Across calibration targets, the gaze span on each axis must be at least this fraction of the
    /// target span on that axis. A near-zero span means the sensor wasn't tracking the user's gaze.
    /// </summary>
    public const double MinGazeVariationRatio = 0.2;

    /// <summary>
    /// Returns true when <paramref name="transform"/> maps screen-space reference points (the four
    /// corners and the centre) to finite coordinates within <see cref="MaxOffScreenFactor"/> of the
    /// screen bounds. Returns false for a non-positive screen size or any reference point that maps
    /// to a non-finite or far-off-screen coordinate.
    /// </summary>
    public static bool IsTransformSane(ICalibrationTransform transform, double screenWidth, double screenHeight)
    {
        if (!(screenWidth > 0) || !(screenHeight > 0)) return false;

        double marginX = screenWidth * MaxOffScreenFactor;
        double marginY = screenHeight * MaxOffScreenFactor;

        var references = new[]
        {
            new PhysicalPoint(0, 0),
            new PhysicalPoint(screenWidth, 0),
            new PhysicalPoint(0, screenHeight),
            new PhysicalPoint(screenWidth, screenHeight),
            new PhysicalPoint(screenWidth / 2, screenHeight / 2),
        };

        foreach (var reference in references)
        {
            var p = transform.Apply(reference);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return false;
            if (p.X < -marginX || p.X > screenWidth + marginX) return false;
            if (p.Y < -marginY || p.Y > screenHeight + marginY) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true when a raw gaze sample is plausibly real: finite, and (when a screen size is
    /// known) within <see cref="MaxSampleOffScreenFactor"/> of the screen bounds. Used to drop junk
    /// samples during calibration capture so they cannot pollute a target's mean.
    /// </summary>
    public static bool IsPlausibleSample(PhysicalPoint sample, double screenWidth, double screenHeight)
    {
        if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y)) return false;

        // No screen bounds to judge against → accept any finite coordinate.
        if (!(screenWidth > 0) || !(screenHeight > 0)) return true;

        double marginX = screenWidth * MaxSampleOffScreenFactor;
        double marginY = screenHeight * MaxSampleOffScreenFactor;
        if (sample.X < -marginX || sample.X > screenWidth + marginX) return false;
        if (sample.Y < -marginY || sample.Y > screenHeight + marginY) return false;
        return true;
    }

    /// <summary>
    /// Returns true when the collected gaze varied across the calibration targets on both axes.
    /// A collapsed span on either axis (relative to the target span on that axis) means the sensor
    /// was returning a near-constant value regardless of where the user looked, so the calibration
    /// data is unusable. Returns true when there are too few points or the targets barely span an
    /// axis (nothing to judge against).
    /// </summary>
    public static bool HasSufficientGazeVariation(IReadOnlyList<CalibrationPoint> points)
    {
        if (points.Count < 2) return true;

        double minGX = double.MaxValue, maxGX = double.MinValue;
        double minGY = double.MaxValue, maxGY = double.MinValue;
        double minTX = double.MaxValue, maxTX = double.MinValue;
        double minTY = double.MaxValue, maxTY = double.MinValue;

        foreach (var p in points)
        {
            minGX = Math.Min(minGX, p.MeanGaze.X); maxGX = Math.Max(maxGX, p.MeanGaze.X);
            minGY = Math.Min(minGY, p.MeanGaze.Y); maxGY = Math.Max(maxGY, p.MeanGaze.Y);
            minTX = Math.Min(minTX, p.Target.X); maxTX = Math.Max(maxTX, p.Target.X);
            minTY = Math.Min(minTY, p.Target.Y); maxTY = Math.Max(maxTY, p.Target.Y);
        }

        return AxisHasVariation(maxGX - minGX, maxTX - minTX)
            && AxisHasVariation(maxGY - minGY, maxTY - minTY);
    }

    private static bool AxisHasVariation(double gazeSpan, double targetSpan)
    {
        // Targets barely span this axis → nothing to judge gaze variation against.
        if (targetSpan < 1.0) return true;
        return gazeSpan >= targetSpan * MinGazeVariationRatio;
    }

    /// <summary>
    /// Largest post-transform residual in pixels: the distance between each calibration point's
    /// target and where the transform actually maps that point's mean gaze. A small value means the
    /// transform reproduces the calibration data; a large value means a poor (untrustworthy) fit.
    /// Returns 0 when there are no points.
    /// </summary>
    public static double MaxResidualPixels(ICalibrationTransform transform, IReadOnlyList<CalibrationPoint> points)
    {
        double max = 0;
        foreach (var point in points)
        {
            var mapped = transform.Apply(point.MeanGaze);
            double err = mapped.DistanceTo(point.Target);
            if (err > max) max = err;
        }
        return max;
    }
}
