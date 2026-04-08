namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Defines a test collection that disables parallelization.
/// This is required because DpiHelper uses static state that cannot be safely shared across parallel tests.
/// </summary>
[CollectionDefinition("DpiHelper Tests", DisableParallelization = true)]
public class DpiHelperTestCollection
{
}

/// <summary>
/// Comprehensive unit tests for the coordinate type system (LogicalPoint, PhysicalPoint, NormalizedPoint).
/// These tests follow Test-Driven Development (TDD) - RED phase.
///
/// The coordinate system prevents off-by-DPI bugs by providing strong typing for:
/// - LogicalPoint: WPF Device-Independent Units (DIPs) - 96 DPI baseline
/// - PhysicalPoint: Actual screen pixels - hardware resolution
/// - NormalizedPoint: Win32 mouse input range (0-65535) - absolute coordinates
///
/// All tests should FAIL initially as the types don't exist yet (RED phase).
/// Implementation will come in the GREEN phase.
///
/// Note: This test class is part of a non-parallelized collection because it mutates
/// global DpiHelper state. Running these tests in parallel would cause flaky failures.
/// </summary>
[Collection("DpiHelper Tests")]
public class CoordinateTypeTests
{
    private const double Epsilon = 0.0001; // Precision for floating-point comparisons

    #region LogicalPoint to PhysicalPoint Conversion Tests

    /// <summary>
    /// Verifies LogicalPoint to PhysicalPoint conversion at standard 96 DPI (100% scaling).
    /// At 96 DPI, logical and physical coordinates should be identical (1:1 ratio).
    /// </summary>
    [Fact]
    public void LogicalPoint_ToPhysical_ConvertsCorrectly_At96Dpi()
    {
        // Arrange - Standard DPI (100% scaling)
        DpiHelper.Initialize(1.0, 1.0);
        var logical = new LogicalPoint(100.0, 200.0);

        // Act
        var physical = logical.ToPhysical();

        // Assert - At 96 DPI, logical == physical
        Assert.Equal(100.0, physical.X, Epsilon);
        Assert.Equal(200.0, physical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies LogicalPoint to PhysicalPoint conversion at 120 DPI (125% scaling).
    /// At 120 DPI, physical pixels = logical DIPs * 1.25.
    /// </summary>
    [Fact]
    public void LogicalPoint_ToPhysical_ConvertsCorrectly_At120Dpi()
    {
        // Arrange - 125% scaling (120 DPI)
        DpiHelper.Initialize(1.25, 1.25);
        var logical = new LogicalPoint(100.0, 200.0);

        // Act
        var physical = logical.ToPhysical();

        // Assert - Physical should be 1.25x larger
        Assert.Equal(125.0, physical.X, Epsilon);
        Assert.Equal(250.0, physical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies LogicalPoint to PhysicalPoint conversion at 144 DPI (150% scaling).
    /// At 144 DPI, physical pixels = logical DIPs * 1.5.
    /// </summary>
    [Fact]
    public void LogicalPoint_ToPhysical_ConvertsCorrectly_At144Dpi()
    {
        // Arrange - 150% scaling (144 DPI)
        DpiHelper.Initialize(1.5, 1.5);
        var logical = new LogicalPoint(100.0, 200.0);

        // Act
        var physical = logical.ToPhysical();

        // Assert - Physical should be 1.5x larger
        Assert.Equal(150.0, physical.X, Epsilon);
        Assert.Equal(300.0, physical.Y, Epsilon);
    }

    #endregion

    #region PhysicalPoint to LogicalPoint Conversion Tests

    /// <summary>
    /// Verifies PhysicalPoint to LogicalPoint conversion at standard 96 DPI (100% scaling).
    /// At 96 DPI, logical and physical coordinates should be identical (1:1 ratio).
    /// </summary>
    [Fact]
    public void PhysicalPoint_ToLogical_ConvertsCorrectly_At96Dpi()
    {
        // Arrange - Standard DPI (100% scaling)
        DpiHelper.Initialize(1.0, 1.0);
        var physical = new PhysicalPoint(100.0, 200.0);

        // Act
        var logical = physical.ToLogical();

        // Assert - At 96 DPI, physical == logical
        Assert.Equal(100.0, logical.X, Epsilon);
        Assert.Equal(200.0, logical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies PhysicalPoint to LogicalPoint conversion at 120 DPI (125% scaling).
    /// At 120 DPI, logical DIPs = physical pixels / 1.25.
    /// </summary>
    [Fact]
    public void PhysicalPoint_ToLogical_ConvertsCorrectly_At120Dpi()
    {
        // Arrange - 125% scaling (120 DPI)
        DpiHelper.Initialize(1.25, 1.25);
        var physical = new PhysicalPoint(125.0, 250.0);

        // Act
        var logical = physical.ToLogical();

        // Assert - Logical should be 1/1.25 = 0.8x smaller
        Assert.Equal(100.0, logical.X, Epsilon);
        Assert.Equal(200.0, logical.Y, Epsilon);
    }

    #endregion

    #region PhysicalPoint to NormalizedPoint Conversion Tests

    /// <summary>
    /// Verifies PhysicalPoint to NormalizedPoint conversion on a 1920x1080 screen.
    /// Win32 normalized coordinates range from 0 to 65535 across the screen dimensions.
    /// Formula: normalized = (physical / screenSize) * 65535
    /// </summary>
    [Fact]
    public void PhysicalPoint_ToNormalized_ConvertsCorrectly_1920x1080Screen()
    {
        // Arrange - Full HD screen
        var physical = new PhysicalPoint(960.0, 540.0); // Center of 1920x1080
        var screenBounds = new ScreenBounds(1920, 1080);

        // Act
        var normalized = physical.ToNormalized(screenBounds);

        // Assert - Center of screen should be ~32768 (65535 / 2, rounded)
        // 960 / 1920 * 65535 = 32767.5 → rounds to 32768 (banker's rounding)
        // 540 / 1080 * 65535 = 32767.5 → rounds to 32768 (banker's rounding)
        Assert.Equal(32768, normalized.X);
        Assert.Equal(32768, normalized.Y);
    }

    /// <summary>
    /// Verifies NormalizedPoint to PhysicalPoint conversion on a 1920x1080 screen.
    /// Win32 normalized coordinates range from 0 to 65535.
    /// Formula: physical = (normalized / 65535) * screenSize
    /// </summary>
    [Fact]
    public void NormalizedPoint_ToPhysical_ConvertsCorrectly_1920x1080Screen()
    {
        // Arrange - Full HD screen, center point
        var normalized = new NormalizedPoint(32767, 32767);
        var screenBounds = new ScreenBounds(1920, 1080);

        // Act
        var physical = normalized.ToPhysical(screenBounds);

        // Assert - Should convert back to center of screen
        // 32767 / 65535 * 1920 = 959.98... ≈ 960
        // 32767 / 65535 * 1080 = 539.99... ≈ 540
        Assert.Equal(960.0, physical.X, 1.0); // Allow ±1 pixel for rounding
        Assert.Equal(540.0, physical.Y, 1.0);
    }

    #endregion

    #region Round-Trip Conversion Tests

    /// <summary>
    /// Verifies that converting LogicalPoint → PhysicalPoint → LogicalPoint preserves the original value.
    /// This ensures no precision is lost in the conversion process.
    /// </summary>
    [Fact]
    public void LogicalToPhysicalToLogical_RoundTrip_PreservesValue()
    {
        // Arrange - 125% scaling
        DpiHelper.Initialize(1.25, 1.25);
        var original = new LogicalPoint(123.456, 789.012);

        // Act - Convert to physical and back
        var physical = original.ToPhysical();
        var roundTrip = physical.ToLogical();

        // Assert - Should match original within epsilon
        Assert.Equal(original.X, roundTrip.X, Epsilon);
        Assert.Equal(original.Y, roundTrip.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that converting PhysicalPoint → NormalizedPoint → PhysicalPoint preserves the original value.
    /// This ensures coordinate transformations are bijective (reversible).
    /// </summary>
    [Fact]
    public void PhysicalToNormalizedToPhysical_RoundTrip_PreservesValue()
    {
        // Arrange
        var original = new PhysicalPoint(1234.5, 678.9);
        var screenBounds = new ScreenBounds(1920, 1080);

        // Act - Convert to normalized and back
        var normalized = original.ToNormalized(screenBounds);
        var roundTrip = normalized.ToPhysical(screenBounds);

        // Assert - Integer rounding in ToNormalized limits precision to ~screenDim/65535.
        // For 1920x1080, the quantization step is about 0.015 physical pixels.
        const double roundTripTolerance = 0.02;
        Assert.Equal(original.X, roundTrip.X, roundTripTolerance);
        Assert.Equal(original.Y, roundTrip.Y, roundTripTolerance);
    }

    #endregion

    #region Equality Tests

    /// <summary>
    /// Verifies that two LogicalPoints with identical coordinates are considered equal.
    /// Tests both Equals() method and == operator.
    /// </summary>
    [Fact]
    public void LogicalPoint_Equality_SameCoordinates_AreEqual()
    {
        // Arrange
        var point1 = new LogicalPoint(100.0, 200.0);
        var point2 = new LogicalPoint(100.0, 200.0);

        // Assert - Test value equality
        Assert.Equal(point1, point2);
        Assert.True(point1 == point2);
        Assert.False(point1 != point2);
        Assert.True(point1.Equals(point2));
    }

    /// <summary>
    /// Verifies that two PhysicalPoints with identical coordinates are considered equal.
    /// Tests both Equals() method and == operator.
    /// </summary>
    [Fact]
    public void PhysicalPoint_Equality_SameCoordinates_AreEqual()
    {
        // Arrange
        var point1 = new PhysicalPoint(100.0, 200.0);
        var point2 = new PhysicalPoint(100.0, 200.0);

        // Assert - Test value equality
        Assert.Equal(point1, point2);
        Assert.True(point1 == point2);
        Assert.False(point1 != point2);
        Assert.True(point1.Equals(point2));
    }

    /// <summary>
    /// Verifies that two NormalizedPoints with identical coordinates are considered equal.
    /// Tests both Equals() method and == operator.
    /// </summary>
    [Fact]
    public void NormalizedPoint_Equality_SameCoordinates_AreEqual()
    {
        // Arrange
        var point1 = new NormalizedPoint(32767, 32767);
        var point2 = new NormalizedPoint(32767, 32767);

        // Assert - Test value equality
        Assert.Equal(point1, point2);
        Assert.True(point1 == point2);
        Assert.False(point1 != point2);
        Assert.True(point1.Equals(point2));
    }

    /// <summary>
    /// Verifies that two LogicalPoints with different coordinates are not equal.
    /// </summary>
    [Fact]
    public void LogicalPoint_Equality_DifferentCoordinates_AreNotEqual()
    {
        // Arrange
        var point1 = new LogicalPoint(100.0, 200.0);
        var point2 = new LogicalPoint(100.0, 201.0);

        // Assert
        Assert.NotEqual(point1, point2);
        Assert.False(point1 == point2);
        Assert.True(point1 != point2);
    }

    /// <summary>
    /// Verifies that two PhysicalPoints with different coordinates are not equal.
    /// </summary>
    [Fact]
    public void PhysicalPoint_Equality_DifferentCoordinates_AreNotEqual()
    {
        // Arrange
        var point1 = new PhysicalPoint(100.0, 200.0);
        var point2 = new PhysicalPoint(101.0, 200.0);

        // Assert
        Assert.NotEqual(point1, point2);
        Assert.False(point1 == point2);
        Assert.True(point1 != point2);
    }

    /// <summary>
    /// Verifies that two NormalizedPoints with different coordinates are not equal.
    /// </summary>
    [Fact]
    public void NormalizedPoint_Equality_DifferentCoordinates_AreNotEqual()
    {
        // Arrange
        var point1 = new NormalizedPoint(32767, 32767);
        var point2 = new NormalizedPoint(32768, 32767);

        // Assert
        Assert.NotEqual(point1, point2);
        Assert.False(point1 == point2);
        Assert.True(point1 != point2);
    }

    /// <summary>
    /// Verifies that equal LogicalPoints produce the same hash code.
    /// This is critical for using coordinates as dictionary keys or in hash sets.
    /// </summary>
    [Fact]
    public void LogicalPoint_GetHashCode_SameCoordinates_SameHashCode()
    {
        // Arrange
        var point1 = new LogicalPoint(100.0, 200.0);
        var point2 = new LogicalPoint(100.0, 200.0);

        // Assert - Equal objects must have equal hash codes
        Assert.Equal(point1.GetHashCode(), point2.GetHashCode());
    }

    /// <summary>
    /// Verifies that equal PhysicalPoints produce the same hash code.
    /// </summary>
    [Fact]
    public void PhysicalPoint_GetHashCode_SameCoordinates_SameHashCode()
    {
        // Arrange
        var point1 = new PhysicalPoint(100.0, 200.0);
        var point2 = new PhysicalPoint(100.0, 200.0);

        // Assert
        Assert.Equal(point1.GetHashCode(), point2.GetHashCode());
    }

    /// <summary>
    /// Verifies that equal NormalizedPoints produce the same hash code.
    /// </summary>
    [Fact]
    public void NormalizedPoint_GetHashCode_SameCoordinates_SameHashCode()
    {
        // Arrange
        var point1 = new NormalizedPoint(32767, 32767);
        var point2 = new NormalizedPoint(32767, 32767);

        // Assert
        Assert.Equal(point1.GetHashCode(), point2.GetHashCode());
    }

    #endregion

    #region Arithmetic Operation Tests

    /// <summary>
    /// Verifies that adding two PhysicalPoints produces the correct result.
    /// Addition should work component-wise: (x1, y1) + (x2, y2) = (x1+x2, y1+y2).
    /// </summary>
    [Fact]
    public void PhysicalPoint_Add_ReturnsCorrectValue()
    {
        // Arrange
        var point1 = new PhysicalPoint(100.0, 200.0);
        var point2 = new PhysicalPoint(50.0, 75.0);

        // Act
        var result = point1 + point2;

        // Assert
        Assert.Equal(150.0, result.X, Epsilon);
        Assert.Equal(275.0, result.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that adding two LogicalPoints produces the correct result.
    /// </summary>
    [Fact]
    public void LogicalPoint_Add_ReturnsCorrectValue()
    {
        // Arrange
        var point1 = new LogicalPoint(100.0, 200.0);
        var point2 = new LogicalPoint(50.0, 75.0);

        // Act
        var result = point1 + point2;

        // Assert
        Assert.Equal(150.0, result.X, Epsilon);
        Assert.Equal(275.0, result.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that subtracting two PhysicalPoints produces the correct result.
    /// Subtraction should work component-wise: (x1, y1) - (x2, y2) = (x1-x2, y1-y2).
    /// </summary>
    [Fact]
    public void PhysicalPoint_Subtract_ReturnsCorrectValue()
    {
        // Arrange
        var point1 = new PhysicalPoint(100.0, 200.0);
        var point2 = new PhysicalPoint(50.0, 75.0);

        // Act
        var result = point1 - point2;

        // Assert
        Assert.Equal(50.0, result.X, Epsilon);
        Assert.Equal(125.0, result.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that subtracting two LogicalPoints produces the correct result.
    /// </summary>
    [Fact]
    public void LogicalPoint_Subtract_ReturnsCorrectValue()
    {
        // Arrange
        var point1 = new LogicalPoint(100.0, 200.0);
        var point2 = new LogicalPoint(50.0, 75.0);

        // Act
        var result = point1 - point2;

        // Assert
        Assert.Equal(50.0, result.X, Epsilon);
        Assert.Equal(125.0, result.Y, Epsilon);
    }

    #endregion

    #region Edge Case Tests

    /// <summary>
    /// Verifies that converting a zero LogicalPoint to PhysicalPoint produces a zero PhysicalPoint.
    /// Edge case: origin (0,0) should remain origin through all transformations.
    /// </summary>
    [Fact]
    public void LogicalPoint_Conversion_ZeroPoint_ReturnsZeroPoint()
    {
        // Arrange
        DpiHelper.Initialize(1.5, 1.5);
        var zero = new LogicalPoint(0.0, 0.0);

        // Act
        var physical = zero.ToPhysical();

        // Assert
        Assert.Equal(0.0, physical.X, Epsilon);
        Assert.Equal(0.0, physical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that converting a zero PhysicalPoint to NormalizedPoint produces a zero NormalizedPoint.
    /// Edge case: origin should map to Win32 normalized origin.
    /// </summary>
    [Fact]
    public void PhysicalPoint_Conversion_ZeroPoint_ReturnsZeroPoint()
    {
        // Arrange
        var zero = new PhysicalPoint(0.0, 0.0);
        var screenBounds = new ScreenBounds(1920, 1080);

        // Act
        var normalized = zero.ToNormalized(screenBounds);

        // Assert
        Assert.Equal(0, normalized.X);
        Assert.Equal(0, normalized.Y);
    }

    /// <summary>
    /// Verifies that negative coordinates are handled correctly during conversion.
    /// Edge case: negative coordinates can occur with multi-monitor setups where
    /// secondary monitors are positioned left/above the primary monitor.
    /// </summary>
    [Fact]
    public void LogicalPoint_Conversion_NegativeCoordinates_HandlesCorrectly()
    {
        // Arrange - Multi-monitor scenario
        DpiHelper.Initialize(1.5, 1.5);
        var negative = new LogicalPoint(-100.0, -200.0);

        // Act
        var physical = negative.ToPhysical();

        // Assert - Negative values should scale correctly
        Assert.Equal(-150.0, physical.X, Epsilon);
        Assert.Equal(-300.0, physical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that negative PhysicalPoint coordinates convert correctly to LogicalPoint.
    /// </summary>
    [Fact]
    public void PhysicalPoint_Conversion_NegativeCoordinates_HandlesCorrectly()
    {
        // Arrange
        DpiHelper.Initialize(2.0, 2.0);
        var negative = new PhysicalPoint(-200.0, -400.0);

        // Act
        var logical = negative.ToLogical();

        // Assert
        Assert.Equal(-100.0, logical.X, Epsilon);
        Assert.Equal(-200.0, logical.Y, Epsilon);
    }

    /// <summary>
    /// Verifies that very large coordinates (near screen bounds) are handled correctly.
    /// Edge case: 4K monitor (3840x2160) coordinates.
    /// </summary>
    [Fact]
    public void PhysicalPoint_Conversion_LargeCoordinates_HandlesCorrectly()
    {
        // Arrange - 4K screen, bottom-right corner
        var large = new PhysicalPoint(3840.0, 2160.0);
        var screenBounds = new ScreenBounds(3840, 2160);

        // Act
        var normalized = large.ToNormalized(screenBounds);

        // Assert - Should map to maximum Win32 normalized value
        Assert.Equal(65535, normalized.X);
        Assert.Equal(65535, normalized.Y);
    }

    #endregion

    #region Type Safety Tests

    /// <summary>
    /// Verifies that LogicalPoint and PhysicalPoint are distinct types and cannot be
    /// accidentally assigned to each other. This is a compile-time safety check.
    /// If this test compiles, it means the types are not properly separated.
    /// </summary>
    [Fact]
    public void CoordinateTypes_AreDifferentTypes_PreventAccidentalAssignment()
    {
        // Arrange
        var logical = new LogicalPoint(100.0, 200.0);
        var physical = new PhysicalPoint(100.0, 200.0);

        // Assert - These should be different types
        Assert.IsType<LogicalPoint>(logical);
        Assert.IsType<PhysicalPoint>(physical);
        Assert.IsNotType<PhysicalPoint>(logical);
        Assert.IsNotType<LogicalPoint>(physical);
    }

    /// <summary>
    /// Verifies that explicit conversion is required between coordinate types.
    /// This prevents accidental misuse of coordinates in different coordinate spaces.
    /// </summary>
    [Fact]
    public void CoordinateTypes_RequireExplicitConversion_PreventImplicitMisuse()
    {
        // Arrange
        DpiHelper.Initialize(1.5, 1.5);
        var logical = new LogicalPoint(100.0, 200.0);

        // Act - Must explicitly convert
        var physical = logical.ToPhysical();

        // Assert - Conversion should produce different values at non-100% DPI
        Assert.NotEqual(logical.X, physical.X);
        Assert.NotEqual(logical.Y, physical.Y);
    }

    #endregion

    #region ToString Tests

    /// <summary>
    /// Verifies that LogicalPoint has a meaningful ToString() representation.
    /// Useful for debugging and logging.
    /// </summary>
    [Fact]
    public void LogicalPoint_ToString_ReturnsReadableFormat()
    {
        // Arrange
        var point = new LogicalPoint(123.45, 678.90);

        // Act
        var result = point.ToString();

        // Assert - Should contain coordinate values
        Assert.Contains("123.45", result);
        Assert.Contains("678.90", result);
    }

    /// <summary>
    /// Verifies that PhysicalPoint has a meaningful ToString() representation.
    /// </summary>
    [Fact]
    public void PhysicalPoint_ToString_ReturnsReadableFormat()
    {
        // Arrange
        var point = new PhysicalPoint(123.45, 678.90);

        // Act
        var result = point.ToString();

        // Assert
        Assert.Contains("123.45", result);
        Assert.Contains("678.90", result);
    }

    /// <summary>
    /// Verifies that NormalizedPoint has a meaningful ToString() representation.
    /// </summary>
    [Fact]
    public void NormalizedPoint_ToString_ReturnsReadableFormat()
    {
        // Arrange
        var point = new NormalizedPoint(32767, 32767);

        // Act
        var result = point.ToString();

        // Assert
        Assert.Contains("32767", result);
    }

    #endregion

    #region ScreenBounds Tests

    /// <summary>
    /// Verifies that ScreenBounds stores width and height correctly.
    /// ScreenBounds represents the physical dimensions of a display.
    /// </summary>
    [Fact]
    public void ScreenBounds_Constructor_StoresWidthAndHeight()
    {
        // Arrange & Act
        var bounds = new ScreenBounds(1920, 1080);

        // Assert
        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1080, bounds.Height);
    }

    /// <summary>
    /// Verifies that ScreenBounds with equal dimensions are considered equal.
    /// </summary>
    [Fact]
    public void ScreenBounds_Equality_SameDimensions_AreEqual()
    {
        // Arrange
        var bounds1 = new ScreenBounds(1920, 1080);
        var bounds2 = new ScreenBounds(1920, 1080);

        // Assert
        Assert.Equal(bounds1, bounds2);
        Assert.True(bounds1 == bounds2);
        Assert.False(bounds1 != bounds2);
    }

    /// <summary>
    /// Verifies that ScreenBounds with different dimensions are not equal.
    /// </summary>
    [Fact]
    public void ScreenBounds_Equality_DifferentDimensions_AreNotEqual()
    {
        // Arrange
        var bounds1 = new ScreenBounds(1920, 1080);
        var bounds2 = new ScreenBounds(2560, 1440);

        // Assert
        Assert.NotEqual(bounds1, bounds2);
        Assert.False(bounds1 == bounds2);
        Assert.True(bounds1 != bounds2);
    }

    #endregion
}
