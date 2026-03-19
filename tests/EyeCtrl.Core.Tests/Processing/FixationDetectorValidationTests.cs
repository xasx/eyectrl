namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;

/// <summary>
/// Tests for FixationDetector parameter validation.
/// Bug #14: Constructor doesn't validate that fixationRadiusPixels and fixationTimeMs are positive.
/// </summary>
public class FixationDetectorValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public void Constructor_ZeroOrNegativeRadius_ThrowsArgumentOutOfRangeException(double radius)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixationDetector(fixationRadiusPixels: radius, fixationTimeMs: 200));

        Assert.Equal("fixationRadiusPixels", exception.ParamName);
        Assert.Equal(radius, exception.ActualValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Constructor_ZeroOrNegativeTime_ThrowsArgumentOutOfRangeException(int timeMs)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: timeMs));

        Assert.Equal("fixationTimeMs", exception.ParamName);
        Assert.Equal(timeMs, exception.ActualValue);
    }

    [Fact]
    public void Constructor_NaNRadius_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new FixationDetector(fixationRadiusPixels: double.NaN, fixationTimeMs: 200));

        Assert.Equal("fixationRadiusPixels", exception.ParamName);
    }

    [Fact]
    public void Constructor_InfinityRadius_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new FixationDetector(fixationRadiusPixels: double.PositiveInfinity, fixationTimeMs: 200));

        Assert.Equal("fixationRadiusPixels", exception.ParamName);
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        // Should not throw with valid positive values
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        Assert.NotNull(detector);
    }

    [Fact]
    public void Constructor_SmallValidParameters_DoesNotThrow()
    {
        // Should accept very small but positive values
        var detector = new FixationDetector(fixationRadiusPixels: 0.001, fixationTimeMs: 1);
        Assert.NotNull(detector);
    }
}
