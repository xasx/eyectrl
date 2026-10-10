namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Threading;

/// <summary>
/// Review 01 M4: the whole-run cap's logic, pinned without ever arming the real
/// <see cref="Environment.FailFast(string)"/> action. The production cap is 30 s, so these tests
/// drive the injected-cap constructor: one proves the action fires when nothing disarms the
/// watchdog, one proves it does not fire when the run disposes it in time.
/// </summary>
public class ProbeRingRunWatchdogTests
{
    [Fact]
    public void TheProductionCap_IsThirtySeconds() =>
        Check.WithCustomMessage("Requirement (h) asks for about 30 s; the watchdog's cap is the bound.")
            .That(ProbeRingRunWatchdog.Cap).IsEqualTo(TimeSpan.FromSeconds(30));

    [Fact]
    public void Watchdog_FiresWhenTheCapExpires()
    {
        using var fired = new ManualResetEventSlim(false);
        using var watchdog = new ProbeRingRunWatchdog(TimeSpan.FromMilliseconds(50), fired.Set);

        Check.WithCustomMessage(
                "The watchdog must invoke its timeout action once the cap expires; without it a stalled run "
                + "leaves the topmost window on the owner's screen.")
            .That(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).IsTrue();
    }

    [Fact]
    public void Watchdog_DoesNotFire_WhenDisposedInTime()
    {
        using var fired = new ManualResetEventSlim(false);
        using (new ProbeRingRunWatchdog(TimeSpan.FromSeconds(10), fired.Set))
        {
            // The run finishes; Dispose disarms it.
        }

        Check.WithCustomMessage(
                "A run that completes and disposes its watchdog must never have its timeout action fire.")
            .That(fired.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)).IsFalse();
    }
}
