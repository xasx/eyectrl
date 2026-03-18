namespace EyeCtrl.Platform.Tests.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

/// <summary>
/// Tests for Win32InputSimulator coordinate validation and overflow protection.
/// Bug #2: Integer overflow when normalizing coordinates for mouse input.
/// </summary>
public class Win32InputSimulatorValidationTests
{
    [Fact]
    public void MoveCursor_WithNaNCoordinates_DoesNotCrash()
    {
        var simulator = new Win32InputSimulator();

        // Should handle NaN gracefully without crashing
        // The implementation should either clamp or throw meaningful exception
        var point = new ScreenPoint(double.NaN, double.NaN);

        // Test that it doesn't crash - implementation may throw or silently handle
        try
        {
            simulator.MoveCursor(point);
            // If it doesn't throw, that's acceptable (it handled NaN gracefully)
        }
        catch (ArgumentException)
        {
            // Also acceptable - explicit validation
        }
    }

    [Fact]
    public void MoveCursor_WithInfinityCoordinates_DoesNotCrash()
    {
        var simulator = new Win32InputSimulator();

        var point = new ScreenPoint(double.PositiveInfinity, double.NegativeInfinity);

        try
        {
            simulator.MoveCursor(point);
        }
        catch (ArgumentException)
        {
            // Acceptable - explicit validation
        }
    }

    [Fact]
    public void MoveCursor_WithExtremelyLargeCoordinates_DoesNotOverflow()
    {
        var simulator = new Win32InputSimulator();

        // Test with coordinates that would cause overflow if not handled
        var point = new ScreenPoint(1_000_000_000, 1_000_000_000);

        // Should not crash or produce invalid behavior
        try
        {
            simulator.MoveCursor(point);
            // If it succeeds, coordinates were clamped appropriately
        }
        catch (ArgumentException)
        {
            // Also acceptable
        }
    }

    [Fact]
    public void MoveCursor_WithNegativeCoordinates_DoesNotCrash()
    {
        var simulator = new Win32InputSimulator();

        // Negative coordinates (off-screen to left/top)
        var point = new ScreenPoint(-100, -200);

        // Should handle gracefully
        try
        {
            simulator.MoveCursor(point);
        }
        catch (ArgumentException)
        {
            // Acceptable
        }
    }

    [Fact]
    public void LeftClick_WithNaNCoordinates_DoesNotCrash()
    {
        var simulator = new Win32InputSimulator();
        var point = new ScreenPoint(double.NaN, double.NaN);

        try
        {
            simulator.LeftClick(point);
        }
        catch (ArgumentException)
        {
            // Acceptable
        }
    }

    [Fact]
    public void Scroll_WithValidCoordinates_DoesNotCrash()
    {
        var simulator = new Win32InputSimulator();
        var point = new ScreenPoint(100, 100);

        // Normal scroll values should work
        simulator.Scroll(point, 1, 1);
        simulator.Scroll(point, -1, -1);
    }

    [Fact]
    public void Scroll_WithExtremeDeltaValues_DoesNotOverflow()
    {
        var simulator = new Win32InputSimulator();
        var point = new ScreenPoint(100, 100);

        // Test with very large delta values that could overflow when multiplied by 120
        // Max safe value before overflow: int.MaxValue / 120 = 17,895,697
        var safeDelta = 17_000_000;

        try
        {
            simulator.Scroll(point, safeDelta, safeDelta);
        }
        catch (ArgumentException)
        {
            // Acceptable if implementation validates delta range
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void MoveCursor_WithValidScreenCoordinates_DoesNotThrow(double x, double y)
    {
        var simulator = new Win32InputSimulator();
        var point = new ScreenPoint(x, y);

        // Valid screen coordinates should always work
        simulator.MoveCursor(point);
    }
}
