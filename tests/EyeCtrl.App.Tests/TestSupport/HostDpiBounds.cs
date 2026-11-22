namespace EyeCtrl.App.Tests.TestSupport;

using System;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Core.Services;
using NFluent;

/// <summary>
/// The one honest comparison between a REAL WPF window's bounds and the placement a note manager
/// derived. The placement is snapped to the SIMULATED device grid the test sets with
/// <c>DpiHelper.Initialize</c> (fixup-02 F4's <c>NoteLayout.WithRevealTargetClearance</c>), but WPF
/// stores a window's Left/Top (and a size set while the window already owns an HWND) on the HOST
/// monitor's grid. So:
/// <list type="bullet">
/// <item>the comparison is EXACT when the host's real scale IS the simulated scale — production's
/// case, where <c>DpiHelper</c> is the real DPI, and this dev machine at 200 %;</item>
/// <item>otherwise it allows ONE HOST DEVICE PIXEL (<c>1 / hostScale</c> DIP) — exactly the HWND's
/// own snap and nothing more. The CI runner is 1.0, so a placement's half DIP (358.5, a whole
/// device pixel at the simulated 2.0) legitimately arrives as 359.</item>
/// </list>
/// The host scale is read from the real window itself (<see cref="VisualTreeHelper.GetDpi(Visual)"/>,
/// the pattern <c>NoteKeyboardRealProbeTests</c> already uses), never assumed.
/// </summary>
internal static class HostDpiBounds
{
    /// <summary>A hair over the tolerance so an exact device-pixel edge is not lost to binary
    /// representation of <c>1 / hostScale</c>.</summary>
    internal const double Slack = 1e-9;

    /// <summary>The DIP tolerance for one bound: zero when the host scale equals the simulated
    /// scale, otherwise one host device pixel plus <see cref="Slack"/>.</summary>
    internal static double ToleranceFor(double simulatedScale, double hostScale) =>
        Math.Abs(hostScale - simulatedScale) <= Slack ? 0.0 : (1.0 / hostScale) + Slack;

    /// <summary>Whether <paramref name="actual"/> is <paramref name="expected"/> within
    /// <see cref="ToleranceFor"/> for one axis. Used by <see cref="AssertWindowMatches"/> and
    /// directly pinned by <c>HostDpiBoundsTests</c> against the CI numbers.</summary>
    internal static bool Within(double actual, double expected, double simulatedScale, double hostScale) =>
        Math.Abs(actual - expected) <= ToleranceFor(simulatedScale, hostScale);

    /// <summary>Asserts all four bounds of a real window against the placement's window rect,
    /// each on its own axis (WPF scales both axes together in every configuration this app runs
    /// on, but the two tolerances stay axis-correct).</summary>
    internal static void AssertWindowMatches(Window window, Rect expected, string what)
    {
        var host = VisualTreeHelper.GetDpi(window);
        AssertOne(what, "Width", window.Width, expected.Width, DpiHelper.ScaleX, host.DpiScaleX);
        AssertOne(what, "Height", window.Height, expected.Height, DpiHelper.ScaleY, host.DpiScaleY);
        AssertOne(what, "Left", window.Left, expected.Left, DpiHelper.ScaleX, host.DpiScaleX);
        AssertOne(what, "Top", window.Top, expected.Top, DpiHelper.ScaleY, host.DpiScaleY);
    }

    private static void AssertOne(string what, string bound, double actual, double expected,
        double simulatedScale, double hostScale)
    {
        double tolerance = ToleranceFor(simulatedScale, hostScale);
        Check.WithCustomMessage(
                $"{what}: {bound} {actual} must be the placement's {expected} "
                + $"(host DPI {hostScale}, simulated {simulatedScale}, "
                + $"{(tolerance == 0 ? "exact" : $"one host device pixel = {tolerance} DIP")})")
            .That(Within(actual, expected, simulatedScale, hostScale)).IsTrue();
    }
}
