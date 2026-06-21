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
        ArgumentOutOfRangeException? exception = null;
        try { new OneEuroFilter(minCutoff: minCutoff, beta: 0.5, dCutoff: 1.0); }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("minCutoff");
        Check.That(exception.ActualValue).IsEqualTo(minCutoff);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.5)]
    public void Constructor_NegativeBeta_ThrowsArgumentOutOfRangeException(double beta)
    {
        ArgumentOutOfRangeException? exception = null;
        try { new OneEuroFilter(minCutoff: 5.0, beta: beta, dCutoff: 1.0); }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("beta");
        Check.That(exception.ActualValue).IsEqualTo(beta);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ZeroOrNegativeDCutoff_ThrowsArgumentOutOfRangeException(double dCutoff)
    {
        ArgumentOutOfRangeException? exception = null;
        try { new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: dCutoff); }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("dCutoff");
        Check.That(exception.ActualValue).IsEqualTo(dCutoff);
    }

    [Fact]
    public void Constructor_NaNParameters_ThrowsArgumentException()
    {
        Check.ThatCode(
            () => new OneEuroFilter(minCutoff: double.NaN, beta: 0.5, dCutoff: 1.0)).Throws<ArgumentException>();

        Check.ThatCode(
            () => new OneEuroFilter(minCutoff: 5.0, beta: double.NaN, dCutoff: 1.0)).Throws<ArgumentException>();

        Check.ThatCode(
            () => new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: double.NaN)).Throws<ArgumentException>();
    }

    [Fact]
    public void Constructor_InfinityParameters_ThrowsArgumentException()
    {
        Check.ThatCode(
            () => new OneEuroFilter(minCutoff: double.PositiveInfinity, beta: 0.5, dCutoff: 1.0)).Throws<ArgumentException>();
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var filter = new OneEuroFilter(minCutoff: 5.0, beta: 0.5, dCutoff: 1.0);
        Check.That(filter).IsNotNull();
    }

    [Fact]
    public void Constructor_DefaultParameters_DoesNotThrow()
    {
        var filter = new OneEuroFilter();
        Check.That(filter).IsNotNull();
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

        Check.That(double.IsFinite(result2.X)).IsTrue();
        Check.That(double.IsFinite(result2.Y)).IsTrue();
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

        Check.That(double.IsFinite(result2.X)).IsTrue();
        Check.That(double.IsFinite(result2.Y)).IsTrue();
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

        Check.That(double.IsFinite(result2.X)).IsTrue();
        Check.That(double.IsFinite(result2.Y)).IsTrue();
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsFiniteValues()
    {
        var filter = new OneEuroFilter();

        // First valid point
        filter.Filter(new GazePoint(100, 100, 0, true));

        // Invalid point should return last valid position
        var result = filter.Filter(new GazePoint(0, 0, 100, false));

        Check.That(double.IsFinite(result.X)).IsTrue();
        Check.That(double.IsFinite(result.Y)).IsTrue();
    }
}
