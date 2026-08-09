namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;
using Xunit;

public class ClampCaptureRectTests
{
    [Fact]
    public void Compute_CenteredRegion_ReturnsExpectedRect()
    {
        var result = CaptureRegionMath.Compute(500, 400, 50, 1920, 1080);

        Check.That(result).IsNotNull();
        Check.That(result!.Value.x).IsEqualTo(450);
        Check.That(result.Value.y).IsEqualTo(350);
        Check.That(result.Value.size).IsEqualTo(100);
    }

    [Fact]
    public void Compute_NearLeftEdge_ClampsToZero()
    {
        var result = CaptureRegionMath.Compute(20, 400, 50, 1920, 1080);

        Check.That(result).IsNotNull();
        Check.That(result!.Value.x).IsEqualTo(0);
        Check.That(result.Value.size).IsEqualTo(100);
    }

    [Fact]
    public void Compute_NearTopEdge_ClampsToZero()
    {
        var result = CaptureRegionMath.Compute(500, 10, 30, 1920, 1080);

        Check.That(result).IsNotNull();
        Check.That(result!.Value.y).IsEqualTo(0);
    }

    [Fact]
    public void Compute_NearRightEdge_ClampsToScreenBounds()
    {
        var result = CaptureRegionMath.Compute(1900, 400, 50, 1920, 1080);

        Check.That(result).IsNotNull();
        Check.That(result!.Value.x + result.Value.size).IsStrictlyLessThan(1921);
    }

    [Fact]
    public void Compute_ZeroRadius_ReturnsNull()
    {
        var result = CaptureRegionMath.Compute(500, 400, 0, 1920, 1080);

        Check.That(result).IsNull();
    }

    [Fact]
    public void Compute_NegativeRadius_ReturnsNull()
    {
        var result = CaptureRegionMath.Compute(500, 400, -10, 1920, 1080);

        Check.That(result).IsNull();
    }

    [Fact]
    public void Compute_CenterAtOrigin_ClampsBothAxes()
    {
        var result = CaptureRegionMath.Compute(0, 0, 50, 1920, 1080);

        Check.That(result).IsNotNull();
        Check.That(result!.Value.x).IsEqualTo(0);
        Check.That(result.Value.y).IsEqualTo(0);
    }
}
