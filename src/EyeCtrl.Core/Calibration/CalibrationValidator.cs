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
    /// A valid calibration fine-tunes gaze that already arrives in screen pixels (the provider delivers
    /// tracker-calibrated coordinates), so it must stay close to identity. Reject any transform that
    /// displaces a screen reference point by more than this fraction of the screen diagonal: that is a
    /// degenerate fit (axis flip, gross scale/shear, or large offset) which maps to plausible-but-wrong
    /// locations and amplifies gaze jitter until fixation cannot hold — not a real correction. A realistic
    /// correction displaces reference points by well under 10% of the diagonal.
    /// </summary>
    public const double MaxIdentityDisplacementScreenFraction = 0.25;

    /// <summary>
    /// The largest post-transform residual must stay within this fraction of the screen diagonal
    /// for a calibration to be accepted.
    /// <para>
    /// Tightened from 0.20 on 2026-09-13. At 0.20 this allowed 907 px on a 3840×2400 display, which is
    /// not a gate at all — it is nine times the width of a keyboard key, so a calibration that made
    /// every key unhittable passed comfortably. The 2026-09-12 fit, with residuals of 450–500 px and
    /// one target 911 px from its mean, was accepted on exactly this margin and then applied to every
    /// gaze point the user produced. 0.05 is 226 px here (5 % of the 4528 px DIAGONAL — not of the
    /// width, which is a different and smaller number it is easy to quote by mistake): still roughly
    /// two keys wide, so a genuine correction is not rejected, but no longer able to admit a
    /// calibration that is worse than none.
    /// </para>
    /// </summary>
    public const double MaxResidualScreenFraction = 0.05;

    /// <summary>
    /// A target whose in-band radial σ exceeds this many pixels is too scattered to calibrate from,
    /// however well the fit happens to pass through it.
    /// <para>
    /// Independent of the residual on purpose: the residual measures whether the transform reproduces
    /// the collected MEANS, and a mean computed from scattered samples can sit exactly on its target
    /// while describing nothing the user did. Set against the interaction tolerance rather than the
    /// device floor — the device manages 1–2 px stationary, so 100 px is very loose, and the intent is
    /// to catch a target built from motion rather than to demand stillness the user cannot produce.
    /// </para>
    /// </summary>
    public const double MaxTargetSigmaRadialPixels = 100.0;

    /// <summary>
    /// A target must have spent at least this fraction of its attempt inside the band.
    /// <para>
    /// The companion to the σ gate, and the one that closes the loophole σ alone leaves: σ is computed
    /// over in-band samples only, so a target the user was on for 3 samples out of 300 reports a
    /// beautiful σ. Below half, the point is built from the exceptions rather than the rule.
    /// </para>
    /// </summary>
    public const double MinTargetCoverage = 0.5;

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
    /// Returns true when <paramref name="transform"/> behaves like a small correction: it maps each of the
    /// screen's four corners and centre to within <see cref="MaxIdentityDisplacementScreenFraction"/> of the
    /// screen diagonal from where that point already sits. Rejects axis flips and gross scale/shear/offset
    /// that map points to plausible-but-wrong locations — the failure mode <see cref="IsTransformSane"/>'s
    /// generous on-screen bound lets through (e.g. a transform that flips X and shifts the centre a third of
    /// the screen, amplifying gaze jitter until fixation cannot hold). Returns false for a non-positive
    /// screen size or any reference point that maps to a non-finite coordinate.
    /// </summary>
    public static bool IsNearIdentity(ICalibrationTransform transform, double screenWidth, double screenHeight)
    {
        if (!(screenWidth > 0) || !(screenHeight > 0)) return false;

        double maxDisplacement =
            Math.Sqrt(screenWidth * screenWidth + screenHeight * screenHeight)
            * MaxIdentityDisplacementScreenFraction;

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
            if (p.DistanceTo(reference) > maxDisplacement) return false;
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

    /// <summary>Why one target failed the quality gate, in terms the user can act on.</summary>
    /// <param name="TargetIndex">Zero-based index in the session's target order.</param>
    /// <param name="Where">Plain-language position, e.g. "top-left".</param>
    /// <param name="Reason">One sentence naming the metric and its value.</param>
    public readonly record struct QualityFailure(int TargetIndex, string Where, string Reason);

    /// <summary>
    /// Every reason this calibration should be refused, target by target — empty when it may be
    /// accepted.
    /// <para>
    /// Returns a LIST rather than a bool because "accuracy too poor" is what the old gate said, and it
    /// left the user nothing to change. Which point failed, and whether it failed for scatter or for
    /// coverage, is the difference between "recalibrate and hope" and "hold the top-left point a
    /// little longer". For a user whose only input is gaze, a retry they cannot aim is expensive.
    /// </para>
    /// </summary>
    /// <param name="transform">The fitted transform under test.</param>
    /// <param name="points">The session's collected points, in presentation order.</param>
    /// <param name="screenSize">Screen size in physical pixels, for the residual budget.</param>
    public static IReadOnlyList<QualityFailure> FindQualityFailures(
        ICalibrationTransform transform,
        IReadOnlyList<CalibrationPoint> points,
        PhysicalPoint screenSize)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(points);

        var failures = new List<QualityFailure>();
        double diagonal = Math.Sqrt((screenSize.X * screenSize.X) + (screenSize.Y * screenSize.Y));
        double residualBudget = diagonal * MaxResidualScreenFraction;

        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var where = DescribePosition(point.Target, screenSize);

            double residual = transform.Apply(point.MeanGaze).DistanceTo(point.Target);
            if (residual > residualBudget)
            {
                failures.Add(new QualityFailure(i, where,
                    $"the fit misses it by {residual:F0}px (limit {residualBudget:F0}px)"));
                continue;   // one reason per target: the most severe, not a pile-on
            }

            double sigmaRadial = Math.Sqrt(
                (point.StdDevX * point.StdDevX) + (point.StdDevY * point.StdDevY));
            if (sigmaRadial > MaxTargetSigmaRadialPixels)
            {
                failures.Add(new QualityFailure(i, where,
                    $"gaze was too scattered on it (spread {sigmaRadial:F0}px, limit {MaxTargetSigmaRadialPixels:F0}px)"));
                continue;
            }

            // Null coverage means the calibration predates the measurement; it cannot be judged and
            // must not be failed for being old.
            if (point.Coverage is { } coverage && coverage < MinTargetCoverage)
            {
                failures.Add(new QualityFailure(i, where,
                    $"gaze was on it only {coverage * 100:F0}% of the time (needs {MinTargetCoverage * 100:F0}%)"));
            }
        }

        return failures;
    }

    /// <summary>
    /// Renders the failures as one sentence for the user, ending in what to do differently.
    /// </summary>
    public static string DescribeFailures(IReadOnlyList<QualityFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0) return string.Empty;

        var detail = string.Join("; ", failures.Select(f => $"{f.Where} — {f.Reason}"));

        // The advice is the same in every case because the cause almost always is: the user moved
        // during collection. Naming the points is what makes it actionable — they are told WHICH ones
        // to spend longer on, rather than being sent back to repeat all of them blind.
        return $"Calibration not accepted. {detail}. "
             + "Try again and rest on each point until its ring fills — looking away resets nothing, "
             + "so short looks add up.";
    }

    /// <summary>Plain-language screen position of a target, for a message a user reads under stress.</summary>
    private static string DescribePosition(PhysicalPoint target, PhysicalPoint screenSize)
    {
        if (!(screenSize.X > 0) || !(screenSize.Y > 0)) return "a point";

        var nx = target.X / screenSize.X;
        var ny = target.Y / screenSize.Y;

        string vertical = ny <= 1.0 / 3.0 ? "top" : ny >= 2.0 / 3.0 ? "bottom" : "middle";
        string horizontal = nx <= 1.0 / 3.0 ? "left" : nx >= 2.0 / 3.0 ? "right" : "centre";

        if (vertical == "middle" && horizontal == "centre") return "the centre point";
        if (vertical == "middle") return $"the {horizontal} point";
        if (horizontal == "centre") return $"the {vertical} point";
        return $"the {vertical}-{horizontal} point";
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
