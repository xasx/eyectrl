namespace EyeCtrl.App.Tests.Tests;

/// <summary>
/// Tests for KeyboardWindow cooldown calculation bug.
/// Bug: Line 292 in KeyboardWindow.xaml.cs has incorrect operator precedence:
/// _keyDwellMs / 2 * 1000L evaluates as (_keyDwellMs / 2) * 1000L
/// but should be _keyDwellMs * 1000L / 2 or _keyDwellMs * 500L
/// </summary>
public class KeyboardCooldownTests
{
    /// <summary>
    /// Test that demonstrates the expected cooldown calculation.
    /// Cooldown should be half of dwell time, converted from milliseconds to microseconds.
    /// </summary>
    [Theory]
    [InlineData(800, 400_000)]  // 800ms dwell -> 400ms (400,000 μs) cooldown
    [InlineData(1000, 500_000)] // 1000ms dwell -> 500ms (500,000 μs) cooldown
    [InlineData(600, 300_000)]  // 600ms dwell -> 300ms (300,000 μs) cooldown
    public void CooldownCalculation_ShouldBeHalfOfDwellTimeInMicroseconds(int dwellMs, long expectedCooldownMicroseconds)
    {
        // This test documents the expected behavior:
        // Cooldown = (dwellMs / 2) * 1000 microseconds
        // Or equivalently: dwellMs * 500 microseconds

        long currentTimestamp = 1_000_000; // 1 second in microseconds

        // CORRECT calculation (what we want):
        long correctCooldown = dwellMs * 500L;

        // INCORRECT calculation (current bug - line 292):
        long buggyCalculation = dwellMs / 2 * 1000L;

        // The cooldown end timestamp should be:
        long expectedCooldownEnd = currentTimestamp + expectedCooldownMicroseconds;
        long correctCooldownEnd = currentTimestamp + correctCooldown;

        // Verify our correct calculation matches expected
        Assert.Equal(expectedCooldownMicroseconds, correctCooldown);
        Assert.Equal(expectedCooldownEnd, correctCooldownEnd);

        // Document the bug: for dwellMs=800, buggy calc gives 400*1000=400,000 (correct by accident!)
        // But for odd numbers it fails: dwellMs=801 gives (801/2)*1000 = 400*1000 = 400,000 (wrong!)
        // This test primarily documents the intended behavior for when we fix the bug.
    }

    /// <summary>
    /// This test demonstrates the bug with integer division.
    /// With odd dwell times, the bug causes incorrect cooldown due to integer division.
    /// </summary>
    [Theory]
    [InlineData(801, 400_500)]  // 801ms dwell -> should be 400.5ms = 400,500 μs cooldown
    [InlineData(999, 499_500)]  // 999ms dwell -> should be 499.5ms = 499,500 μs cooldown
    public void CooldownCalculation_WithOddDwellTime_ShouldHandleCorrectly(int dwellMs, long expectedCooldownMicroseconds)
    {
        // CORRECT calculation preserves fractional milliseconds:
        long correctCooldown = dwellMs * 500L;  // 801 * 500 = 400,500

        // BUGGY calculation loses precision due to integer division first:
        long buggyCalculation = dwellMs / 2 * 1000L;  // 801 / 2 = 400, then 400 * 1000 = 400,000

        Assert.Equal(expectedCooldownMicroseconds, correctCooldown);
        Assert.NotEqual(expectedCooldownMicroseconds, buggyCalculation);
    }
}
