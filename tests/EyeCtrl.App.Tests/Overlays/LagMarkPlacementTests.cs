namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using NFluent;

public class LagMarkPlacementTests
{
    private static readonly Rect Work = new(0, 0, 1920, 1152);       // 1200 minus a 48 DIP taskbar
    private static readonly Size Button = new(160 + 96, 96 + 96);     // the visual plus 2 x 48 DIP hit growth

    [Fact]
    public void WithoutChrome_SitsBottomRight_Inset24()
    {
        var (left, top) = LagMarkPlacement.Place(Work, Button, []);
        Check.That(left).IsEqualTo(1920 - 24 - Button.Width);
        Check.That(top).IsEqualTo(1152 - 24 - Button.Height);
    }

    [Fact]
    public void ChromeInTheCorner_MovesUpAboveIt_WithTheGap()
    {
        var edgeBar = new Rect(1800, 900, 120, 252);
        var (left, top) = LagMarkPlacement.Place(Work, Button, [edgeBar]);
        Check.That(top + Button.Height).IsEqualTo(900 - 24);
        Check.That(left).IsEqualTo(1920 - 24 - Button.Width);
    }

    [Fact]
    public void NeverOverlapsAnyChrome_EvenStacked()
    {
        Rect[] chrome = [new(1800, 900, 120, 252), new(1600, 600, 320, 260)];
        var (left, top) = LagMarkPlacement.Place(Work, Button, chrome);
        var placed = new Rect(left, top, Button.Width, Button.Height);
        foreach (var c in chrome) { var g = c; g.Inflate(24, 24); Check.That(placed.IntersectsWith(g) && !TouchesOnly(placed, g)).IsFalse(); }
        Check.That(top).IsStrictlyGreaterThan(-0.0001);
    }

    [Fact]
    public void WorkAreaOffsetByTheTaskbar_StillPinsToItsBottomRight()
    {
        // The taskbar is at the BOTTOM on this machine: the work area is (0,0,1920,1152).
        // A left-edge taskbar shifts the origin instead; the inset follows the work area, not 0.
        var shifted = new Rect(60, 0, 1860, 1152);
        var (left, top) = LagMarkPlacement.Place(shifted, Button, []);
        Check.That(left + Button.Width).IsEqualTo(60 + 1860 - 24);
        Check.That(top + Button.Height).IsEqualTo(1152 - 24);
    }

    private static bool TouchesOnly(Rect a, Rect b) =>
        a.Right == b.Left || a.Left == b.Right || a.Bottom == b.Top || a.Top == b.Bottom;
}
