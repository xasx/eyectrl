namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Keyboard cooldown behavior, exercised through the real engine: KeyboardWindow.LoadLayout
/// feeds DwellSettings.DwellCooldownMs into a DwellDetector, which converts it from
/// milliseconds to microseconds when gating re-activation after a dwell fires.
/// </summary>
public class KeyboardCooldownTests
{
    /// <summary>
    /// After an activation at t = 1s, a re-dwell whose completion lands before
    /// 1s + cooldown must not fire, and one completing after the cooldown must — pinning the
    /// ms→μs conversion behaviorally (a forgotten ×1000 re-fires during phase 2; a ×1,000,000
    /// never re-fires in phase 3).
    /// </summary>
    [Theory]
    [InlineData(400, 400_000)]  // 400ms cooldown -> 400,000 μs
    [InlineData(500, 500_000)]  // 500ms cooldown -> 500,000 μs
    [InlineData(300, 300_000)]  // 300ms cooldown -> 300,000 μs
    public void CooldownCalculation_ShouldConvertMsToMicroseconds(int cooldownMs, long expectedCooldownMicroseconds)
    {
        var detector = new DwellDetector(cooldownMs: cooldownMs);
        detector.AddTarget(new DwellTarget("key", new PhysicalPoint(100, 100), radius: 50, dwellTimeMs: 200));

        var activations = 0;
        detector.TargetActivated += _ => activations++;

        // Gaze far off-target through t=750ms so the dwell ENTERS at t=800ms (a known stamp);
        // the 200ms dwell then completes exactly at t=1,000,000μs. Samples ride a 50ms cadence
        // (under the detector's 150ms credited-gap bound, so no dwell restarts).
        for (long t = 0; t < 800_000; t += 50_000)
            detector.Update(new PhysicalPoint(5000, 5000), t);
        for (long t = 800_000; t <= 1_000_000; t += 50_000)
            detector.Update(new PhysicalPoint(100, 100), t);
        Check.That(activations).IsEqualTo(1);

        // Re-dwell attempt: gaze stays on target through the cooldown, ending strictly before
        // it expires. Every Update inside the window resets accumulation and returns — no
        // re-activation may fire.
        long cooldownEnd = 1_000_000 + expectedCooldownMicroseconds;
        for (long t = 1_050_000; t < cooldownEnd; t += 50_000)
            detector.Update(new PhysicalPoint(100, 100), t);
        Check.That(activations).IsEqualTo(1);

        // First sample at/after cooldownEnd clears the cooldown latch; the re-dwell (entered at
        // cooldownEnd, 200ms dwell) activates exactly at cooldownEnd + 200,000μs.
        for (long t = cooldownEnd; t <= cooldownEnd + 200_000; t += 50_000)
            detector.Update(new PhysicalPoint(100, 100), t);
        Check.That(activations).IsEqualTo(2);
    }
}
