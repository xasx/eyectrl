namespace EyeCtrl.App.Tests;

using EyeCtrl.App.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The proof for <see cref="HostDpiBounds"/>, independent of this machine's DPI. It pins the two
/// real-window comparisons it serves (<c>NoteBadgeTests.TheWindow_GrowsUpByTheProtrusion…</c> and
/// <c>NoteWindowGeometryTests.PinWindowAndButtons</c>): the simulated DpiHelper scale is 2.0, the
/// CI runner's real host scale is 1.0, and the placement's half-DIP Top/Top+Height arrives at the
/// real (unshown) window as the next whole DIP. The 359 vs 358.5 pair below is the exact failure
/// from CI run 45058318223. Pure arithmetic — no window, no DPI write.
/// </summary>
public class HostDpiBoundsTests
{
    [Fact]
    public void EqualHostAndSimulatedScales_AreExact_AndAnyDriftFails()
    {
        Check.WithCustomMessage("a host at the simulated 2.0 has no tolerance at all")
            .That(HostDpiBounds.ToleranceFor(2.0, 2.0)).IsEqualTo(0.0);
        Check.That(HostDpiBounds.Within(358.5, 358.5, 2.0, 2.0)).IsTrue();
        Check.WithCustomMessage(
                "exactly one device pixel of drift must still fail when the host IS the simulated "
                + "scale - otherwise the exact production comparison had no teeth")
            .That(HostDpiBounds.Within(359.0, 358.5, 2.0, 2.0)).IsFalse();
    }

    [Fact]
    public void AHostAtOne_AgainstASimulatedTwo_AllowsExactlyOneHostDevicePixel()
    {
        Check.WithCustomMessage("one host device pixel at 1.0 is one whole DIP (plus the hair of Slack)")
            .That(HostDpiBounds.ToleranceFor(2.0, 1.0)).IsCloseTo(1.0, 1e-8);
        Check.WithCustomMessage(
                "the CI pair 359 vs 358.5 must pass at a 1.0 host: WPF rounded the placement's "
                + "half DIP (a whole device pixel at the simulated 2.0) up to the host's whole DIP")
            .That(HostDpiBounds.Within(359.0, 358.5, 2.0, 1.0)).IsTrue();
        Check.WithCustomMessage(
                "359.6 vs 358.5 is 1.1 DIP - MORE than the host's one device pixel - and must fail")
            .That(HostDpiBounds.Within(359.6, 358.5, 2.0, 1.0)).IsFalse();
    }

    [Fact]
    public void AHostAtOnePointFive_AllowsItsOwnDevicePixel_NotTheSimulatedGrids()
    {
        Check.WithCustomMessage("one host device pixel at 1.5 is 2/3 DIP (plus the hair of Slack)")
            .That(HostDpiBounds.ToleranceFor(2.0, 1.5)).IsCloseTo(2.0 / 3.0, 1e-8);
        Check.WithCustomMessage("a host-snapped 358.666... against the placement's 358.5 passes")
            .That(HostDpiBounds.Within(358.5 + (2.0 / 3.0), 358.5, 2.0, 1.5)).IsTrue();
        Check.WithCustomMessage("0.7 DIP of drift is more than the host's device pixel and fails")
            .That(HostDpiBounds.Within(359.2, 358.5, 2.0, 1.5)).IsFalse();
    }
}
