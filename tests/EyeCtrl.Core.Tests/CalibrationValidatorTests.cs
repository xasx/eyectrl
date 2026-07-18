namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationValidatorTests
{
    // The exact degenerate parameters that were persisted to calibration.json on 2026-06-16
    // and silently poisoned the gaze pipeline (mapping screen centre millions of pixels off-screen).
    private static readonly double[] DegenerateParams =
    {
        164.17311285043388, -10147.434961907775, 11285.824336762067,
        -59.27218454102549, 3683.7487218623305, -2847.0947780201545
    };

    // The exact parameters persisted to calibration.json on 2026-07-14. Unlike DegenerateParams, these
    // stay within the generous 1.5x-screen bound (so IsTransformSane accepts them) but flip X and amplify
    // gaze — mapping screen centre ~3150px away — which breaks fixation. IsNearIdentity is the guard for it.
    private static readonly double[] JitterAmplifyingParams =
    {
        -1.4792189234724626, -1.3271156575795569, 3201.4014928696142,
        -0.3181342085970778, 0.5928388160665401, 1170.8048259684977
    };

    private static AffineTransform FromParams(double[] p)
    {
        var t = new AffineTransform();
        t.SetParameters(p);
        return t;
    }

    [Fact]
    public void IsTransformSane_ReturnsTrue_ForIdentity()
    {
        var identity = new AffineTransform(); // defaults to identity

        Check.That(CalibrationValidator.IsTransformSane(identity, 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsTransformSane_ReturnsTrue_ForReasonableCorrection()
    {
        // Modest translation + scale — a realistic calibration correction.
        var t = FromParams(new[] { 1.05, 0.02, 40.0, -0.01, 0.98, -25.0 });

        Check.That(CalibrationValidator.IsTransformSane(t, 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsTransformSane_ReturnsFalse_ForDegenerateRealWorldTransform()
    {
        var t = FromParams(DegenerateParams);

        Check.That(CalibrationValidator.IsTransformSane(t, 3840, 2400)).IsFalse();
    }

    [Fact]
    public void IsTransformSane_ReturnsFalse_ForNonFiniteParams()
    {
        var t = FromParams(new[] { double.NaN, 0, 0, 0, 1, 0 });

        Check.That(CalibrationValidator.IsTransformSane(t, 3840, 2400)).IsFalse();
    }

    [Fact]
    public void IsTransformSane_ReturnsFalse_ForNonPositiveScreen()
    {
        var identity = new AffineTransform();

        Check.That(CalibrationValidator.IsTransformSane(identity, 0, 2400)).IsFalse();
    }

    [Fact]
    public void IsTransformSane_ReturnsTrue_ForJitterAmplifyingTransform_WhichIsWhyIsNearIdentityExists()
    {
        // Documents the gap: the 2026-07-14 transform maps every screen reference point within the generous
        // 1.5x-screen bound, so IsTransformSane alone cannot catch it — it loaded and broke fixation in prod.
        var t = FromParams(JitterAmplifyingParams);

        Check.That(CalibrationValidator.IsTransformSane(t, 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsNearIdentity_ReturnsTrue_ForIdentity()
    {
        var identity = new AffineTransform(); // defaults to identity

        Check.That(CalibrationValidator.IsNearIdentity(identity, 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsNearIdentity_ReturnsTrue_ForReasonableCorrection()
    {
        // The same realistic correction IsTransformSane accepts: modest scale + translation.
        var t = FromParams(new[] { 1.05, 0.02, 40.0, -0.01, 0.98, -25.0 });

        Check.That(CalibrationValidator.IsNearIdentity(t, 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsNearIdentity_ReturnsFalse_ForJitterAmplifyingTransform()
    {
        // The 2026-07-14 file: on-screen (passes IsTransformSane) but far from identity — must be rejected.
        var t = FromParams(JitterAmplifyingParams);

        Check.That(CalibrationValidator.IsNearIdentity(t, 3840, 2400)).IsFalse();
    }

    [Fact]
    public void IsNearIdentity_ReturnsFalse_ForNonPositiveScreen()
    {
        var identity = new AffineTransform();

        Check.That(CalibrationValidator.IsNearIdentity(identity, 0, 2400)).IsFalse();
    }

    [Fact]
    public void MaxResidualPixels_NearZero_WhenTransformReproducesTargets()
    {
        var identity = new AffineTransform();
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(100, 100), new PhysicalPoint(100, 100), 0, 0, 30),
            new(new PhysicalPoint(900, 100), new PhysicalPoint(900, 100), 0, 0, 30),
            new(new PhysicalPoint(100, 900), new PhysicalPoint(100, 900), 0, 0, 30),
        };

        Check.That(CalibrationValidator.MaxResidualPixels(identity, points) < 1.0).IsTrue();
    }

    [Fact]
    public void MaxResidualPixels_Large_WhenTransformDoesNotFitPoints()
    {
        var identity = new AffineTransform();
        // Identity maps gaze→gaze, but the target sits 1000-ish px away from the gaze.
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(1000, 1000), new PhysicalPoint(0, 0), 0, 0, 30),
        };

        Check.That(CalibrationValidator.MaxResidualPixels(identity, points) > 1000.0).IsTrue();
    }

    [Fact]
    public void IsPlausibleSample_True_ForOnScreenPoint()
    {
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(1920, 1200), 3840, 2400)).IsTrue();
    }

    [Fact]
    public void IsPlausibleSample_False_ForNonFinite()
    {
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(double.NaN, 0), 3840, 2400)).IsFalse();
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(0, double.PositiveInfinity), 3840, 2400)).IsFalse();
    }

    [Fact]
    public void IsPlausibleSample_False_ForFarOffScreen()
    {
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(1_000_000, 1200), 3840, 2400)).IsFalse();
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(1920, -50_000), 3840, 2400)).IsFalse();
    }

    [Fact]
    public void IsPlausibleSample_True_WhenScreenUnknown_AndFinite()
    {
        // No screen bounds to judge against → accept any finite coordinate.
        Check.That(CalibrationValidator.IsPlausibleSample(new PhysicalPoint(99999, 99999), 0, 0)).IsTrue();
    }

    [Fact]
    public void HasSufficientGazeVariation_True_WhenGazeTracksTargets()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(768, 480), new PhysicalPoint(780, 470), 0, 0, 30),
            new(new PhysicalPoint(3072, 480), new PhysicalPoint(3060, 500), 0, 0, 30),
            new(new PhysicalPoint(768, 1920), new PhysicalPoint(800, 1900), 0, 0, 30),
            new(new PhysicalPoint(3072, 1920), new PhysicalPoint(3050, 1930), 0, 0, 30),
        };

        Check.That(CalibrationValidator.HasSufficientGazeVariation(points)).IsTrue();
    }

    [Fact]
    public void HasSufficientGazeVariation_False_WhenGazeCollapsedOnOneAxis()
    {
        // The 2026-06-16 signature: gaze X spread across the screen but Y stuck near the top.
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(768, 480), new PhysicalPoint(1, 1), 0, 0, 30),
            new(new PhysicalPoint(3072, 480), new PhysicalPoint(2485, 41), 0, 0, 30),
            new(new PhysicalPoint(3072, 1920), new PhysicalPoint(3718, 61), 0, 0, 30),
            new(new PhysicalPoint(768, 1920), new PhysicalPoint(3707, 61), 0, 0, 30),
        };

        Check.That(CalibrationValidator.HasSufficientGazeVariation(points)).IsFalse();
    }

    [Fact]
    public void HasSufficientGazeVariation_False_WhenGazeConstant()
    {
        // Sensor stuck on a single value regardless of target — no variation on either axis.
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(768, 480), new PhysicalPoint(1, 1), 0, 0, 30),
            new(new PhysicalPoint(1920, 1200), new PhysicalPoint(1, 1), 0, 0, 30),
            new(new PhysicalPoint(3072, 1920), new PhysicalPoint(1, 1), 0, 0, 30),
        };

        Check.That(CalibrationValidator.HasSufficientGazeVariation(points)).IsFalse();
    }
}
