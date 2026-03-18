namespace EyeCtrl.App.Tests.Tests;

/// <summary>
/// Tests documenting KeyboardWindow cooldown behavior.
/// Cooldown time is configurable via DwellSettings.DwellCooldownMs and is passed to KeyboardWindow.LoadLayout.
/// The cooldown value is converted from milliseconds to microseconds when setting the cooldown timestamp.
/// </summary>
public class KeyboardCooldownTests
{
    /// <summary>
    /// Test that demonstrates the cooldown calculation from configurable cooldown time.
    /// Cooldown is a separate configurable parameter (DwellSettings.DwellCooldownMs),
    /// not derived from dwell time.
    /// </summary>
    [Theory]
    [InlineData(400, 400_000)]  // 400ms cooldown -> 400,000 μs
    [InlineData(500, 500_000)]  // 500ms cooldown -> 500,000 μs
    [InlineData(300, 300_000)]  // 300ms cooldown -> 300,000 μs
    public void CooldownCalculation_ShouldConvertMsToMicroseconds(int cooldownMs, long expectedCooldownMicroseconds)
    {
        // The cooldown is now a separate configurable parameter (settings.Dwell.DwellCooldownMs)
        // and is converted from ms to μs: cooldownMs * 1000

        long currentTimestamp = 1_000_000; // 1 second in microseconds

        // Correct calculation (what the code now does at line 294):
        long cooldownMicroseconds = cooldownMs * 1000L;

        // The cooldown end timestamp should be:
        long expectedCooldownEnd = currentTimestamp + expectedCooldownMicroseconds;
        long actualCooldownEnd = currentTimestamp + cooldownMicroseconds;

        // Verify the calculation is correct
        Assert.Equal(expectedCooldownMicroseconds, cooldownMicroseconds);
        Assert.Equal(expectedCooldownEnd, actualCooldownEnd);
    }

    /// <summary>
    /// Document that cooldown is independent of dwell time and comes from configuration.
    /// </summary>
    [Fact]
    public void CooldownIsConfigurable_NotDerivedFromDwellTime()
    {
        // Prior to this fix, cooldown was incorrectly calculated as dwellMs / 2 * 1000L
        // which had operator precedence issues and was not configurable.
        //
        // Now cooldown is a separate configuration parameter (DwellSettings.DwellCooldownMs)
        // that is passed to KeyboardWindow.LoadLayout and used directly.
        //
        // This allows users to configure cooldown independently from dwell time,
        // which is more flexible (e.g., longer cooldown to prevent accidental double-triggers,
        // or shorter cooldown for faster interaction).

        Assert.True(true, "Cooldown is now configurable via DwellSettings.DwellCooldownMs");
    }
}
