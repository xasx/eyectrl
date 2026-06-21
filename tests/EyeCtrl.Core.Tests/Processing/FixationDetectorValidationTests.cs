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
        ArgumentOutOfRangeException? exception = null;
        try { new FixationDetector(fixationRadiusPixels: radius, fixationTimeMs: 200); }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("fixationRadiusPixels");
        Check.That(exception.ActualValue).IsEqualTo(radius);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Constructor_ZeroOrNegativeTime_ThrowsArgumentOutOfRangeException(int timeMs)
    {
        ArgumentOutOfRangeException? exception = null;
        try { new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: timeMs); }
        catch (ArgumentOutOfRangeException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("fixationTimeMs");
        Check.That(exception.ActualValue).IsEqualTo(timeMs);
    }

    [Fact]
    public void Constructor_NaNRadius_ThrowsArgumentException()
    {
        ArgumentException? exception = null;
        try { new FixationDetector(fixationRadiusPixels: double.NaN, fixationTimeMs: 200); }
        catch (ArgumentException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("fixationRadiusPixels");
    }

    [Fact]
    public void Constructor_InfinityRadius_ThrowsArgumentException()
    {
        ArgumentException? exception = null;
        try { new FixationDetector(fixationRadiusPixels: double.PositiveInfinity, fixationTimeMs: 200); }
        catch (ArgumentException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("fixationRadiusPixels");
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        // Should not throw with valid positive values
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        Check.That(detector).IsNotNull();
    }

    [Fact]
    public void Constructor_SmallValidParameters_DoesNotThrow()
    {
        // Should accept very small but positive values
        var detector = new FixationDetector(fixationRadiusPixels: 0.001, fixationTimeMs: 1);
        Check.That(detector).IsNotNull();
    }
}
