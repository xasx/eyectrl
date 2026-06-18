namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Tests for OneEuroFilter parameter validation and division by zero protection.
/// Bug #3: Division by zero risk in Alpha method when dt is zero or very small.
/// </summary>
public class OneEuroFilterValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void Constructor_ZeroOrNegativeMinCutoff_ThrowsArgumentOutOfRangeException(double minCutoff)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OneEuroFilter(minCutoff: minCutoff, beta: 0.5, dCutoff: 1.0));

        Assert.Equal("minCutoff", exception.ParamName);
        Assert.Equal(minCutoff, exception.ActualValue);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.5)]
    public void Constructor_NegativeBeta_ThrowsArgumentOutOfRangeException(double beta)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OneEuroFilter(minCutoff: 5.0, beta: beta, dCutoff: 1.0));

        Assert.Equal("beta", exception.ParamName);
        Assert.Equal(beta, exception.ActualValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ZeroOrNegativeDCutoff_ThrowsArgumentOutOfRangeException(double dCutoff)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: dCutoff));

        Assert.Equal("dCutoff", exception.ParamName);
        Assert.Equal(dCutoff, exception.ActualValue);
    }

    [Fact]
    public void Constructor_NaNParameters_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new OneEuroFilter(minCutoff: double.NaN, beta: 0.5, dCutoff: 1.0));

        Assert.Throws<ArgumentException>(
            () => new OneEuroFilter(minCutoff: 5.0, beta: double.NaN, dCutoff: 1.0));

        Assert.Throws<ArgumentException>(
            () => new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: double.NaN));
    }

    [Fact]
    public void Constructor_InfinityParameters_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new OneEuroFilter(minCutoff: double.PositiveInfinity, beta: 0.5, dCutoff: 1.0));
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var filter = new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: 1.0);
        Assert.NotNull(filter);
    }

    [Fact]
    public void Constructor_DefaultParameters_DoesNotThrow()
    {
        var filter = new OneEuroFilter();
        Assert.NotNull(filter);
    }

    [Fact]
    public void Filter_WithZeroTimestampDifference_UsesDefaultDt()
    {
        // This tests that when timestamps are identical, the filter uses fallback dt
        var filter = new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: 1.0);

        // First point initializes the filter
        var result1 = filter.Filter(new GazePoint(100, 100, 0, true));

        // Second point with same timestamp - should use fallback dt and not crash
        var result2 = filter.Filter(new GazePoint(101, 101, 0, true));

        Assert.True(double.IsFinite(result2.X));
        Assert.True(double.IsFinite(result2.Y));
    }

    [Fact]
    public void Filter_WithNegativeTimestampDifference_UsesDefaultDt()
    {
        // This tests that when timestamp goes backwards, the filter uses fallback dt
        var filter = new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: 1.0);

        // First point at t=1000
        var result1 = filter.Filter(new GazePoint(100, 100, 1000, true));

        // Second point at t=500 (backwards in time) - should use fallback dt
        var result2 = filter.Filter(new GazePoint(101, 101, 500, true));

        Assert.True(double.IsFinite(result2.X));
        Assert.True(double.IsFinite(result2.Y));
    }

    [Fact]
    public void Filter_WithVerySmallTimestampDifference_DoesNotCrash()
    {
        // This tests that very small dt values don't cause numerical issues
        var filter = new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: 1.0);

        // First point
        var result1 = filter.Filter(new GazePoint(100, 100, 0, true));

        // Second point with 1 microsecond difference
        var result2 = filter.Filter(new GazePoint(101, 101, 1, true));

        Assert.True(double.IsFinite(result2.X));
        Assert.True(double.IsFinite(result2.Y));
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsFiniteValues()
    {
        var filter = new OneEuroFilter();

        // First valid point
        filter.Filter(new GazePoint(100, 100, 0, true));

        // Invalid point should return last valid position
        var result = filter.Filter(new GazePoint(0, 0, 100, false));

        Assert.True(double.IsFinite(result.X));
        Assert.True(double.IsFinite(result.Y));
    }
}
