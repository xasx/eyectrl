namespace EyeCtrl.Platform.Tests.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

/// <summary>
/// Tests for MouseCoordinateMapper coordinate validation and overflow protection.
/// Bug #2: Integer overflow when normalizing coordinates for mouse input.
/// These tests validate the coordinate normalization logic without sending actual OS input.
/// </summary>
public class MouseCoordinateMapperTests
{
    [Fact]
    public void NormalizeScreenCoordinate_WithNaNCoordinates_ThrowsArgumentException()
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(double.NaN, double.NaN);

        ArgumentException? exception = null;
        try { mapper.NormalizeScreenCoordinate(point, 1920, 1080); }
        catch (ArgumentException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("point");
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithInfinityCoordinates_ThrowsArgumentException()
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(double.PositiveInfinity, double.NegativeInfinity);

        ArgumentException? exception = null;
        try { mapper.NormalizeScreenCoordinate(point, 1920, 1080); }
        catch (ArgumentException ex) { exception = ex; }

        Check.That(exception).IsNotNull();
        Check.That(exception!.ParamName).IsEqualTo("point");
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithInvalidScreenDimensions_ThrowsInvalidOperationException()
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(100, 100);

        Check.ThatCode(() => mapper.NormalizeScreenCoordinate(point, 0, 1080)).Throws<InvalidOperationException>();
        Check.ThatCode(() => mapper.NormalizeScreenCoordinate(point, 1920, 0)).Throws<InvalidOperationException>();
        Check.ThatCode(() => mapper.NormalizeScreenCoordinate(point, -1920, -1080)).Throws<InvalidOperationException>();
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithExtremelyLargeCoordinates_ClampsToScreenBounds()
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(1_000_000_000, 1_000_000_000);

        var (normalizedX, normalizedY) = mapper.NormalizeScreenCoordinate(point, 1920, 1080);

        // Should be clamped to screen edge (65535)
        Check.That(normalizedX).IsEqualTo(65535);
        Check.That(normalizedY).IsEqualTo(65535);
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithNegativeCoordinates_ClampsToZero()
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(-100, -200);

        var (normalizedX, normalizedY) = mapper.NormalizeScreenCoordinate(point, 1920, 1080);

        // Should be clamped to 0
        Check.That(normalizedX).IsEqualTo(0);
        Check.That(normalizedY).IsEqualTo(0);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0)]
    [InlineData(1920, 1080, 1920, 1080, 65535, 65535)]
    [InlineData(960, 540, 1920, 1080, 32768, 32768)]  // Center of 1920x1080
    [InlineData(100, 100, 1920, 1080, 3413, 6068)]    // Specific coordinate
    public void NormalizeScreenCoordinate_WithValidCoordinates_ReturnsCorrectNormalizedValues(
        double x, double y, int screenWidth, int screenHeight, int expectedX, int expectedY)
    {
        var mapper = new MouseCoordinateMapper();
        var point = new PhysicalPoint(x, y);

        var (normalizedX, normalizedY) = mapper.NormalizeScreenCoordinate(point, screenWidth, screenHeight);

        // Allow small rounding differences
        Check.That(normalizedX >= expectedX - 1 && normalizedX <= expectedX + 1).IsTrue();
        Check.That(normalizedY >= expectedY - 1 && normalizedY <= expectedY + 1).IsTrue();
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithEdgeCoordinates_ReturnsValidRange()
    {
        var mapper = new MouseCoordinateMapper();

        // Test right/bottom edge (last valid pixel)
        var point = new PhysicalPoint(1919, 1079);
        var (normalizedX, normalizedY) = mapper.NormalizeScreenCoordinate(point, 1920, 1080);

        // Should be close to 65535 (but not exceed it); exact value depends on screen resolution.
        // For 1920x1080: x=round(1919/1920*65535)=65502, y=round(1079/1080*65535)=65474.
        Check.That(normalizedX >= 65460 && normalizedX <= 65535).IsTrue();
        Check.That(normalizedY >= 65460 && normalizedY <= 65535).IsTrue();
    }

    [Fact]
    public void NormalizeScreenCoordinate_WithSlightlyOutOfBoundsCoordinates_ClampsGracefully()
    {
        var mapper = new MouseCoordinateMapper();

        // Test slightly out of bounds (common with eye tracking drift)
        var point = new PhysicalPoint(1920.5, 1080.5);
        var (normalizedX, normalizedY) = mapper.NormalizeScreenCoordinate(point, 1920, 1080);

        // Should be clamped to screen edge
        Check.That(normalizedX).IsEqualTo(65535);
        Check.That(normalizedY).IsEqualTo(65535);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    [InlineData(1366, 768)]
    public void NormalizeScreenCoordinate_WithCommonScreenResolutions_HandlesCornerCoordinates(
        int screenWidth, int screenHeight)
    {
        var mapper = new MouseCoordinateMapper();

        // Test all four corners
        var topLeft = mapper.NormalizeScreenCoordinate(new PhysicalPoint(0, 0), screenWidth, screenHeight);
        var topRight = mapper.NormalizeScreenCoordinate(new PhysicalPoint(screenWidth, 0), screenWidth, screenHeight);
        var bottomLeft = mapper.NormalizeScreenCoordinate(new PhysicalPoint(0, screenHeight), screenWidth, screenHeight);
        var bottomRight = mapper.NormalizeScreenCoordinate(new PhysicalPoint(screenWidth, screenHeight), screenWidth, screenHeight);

        Check.That(topLeft).IsEqualTo((0, 0));
        Check.That(topRight).IsEqualTo((65535, 0));
        Check.That(bottomLeft).IsEqualTo((0, 65535));
        Check.That(bottomRight).IsEqualTo((65535, 65535));
    }
}
