namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8's chrome-zone composition, pure: the action menu's window square and the modes menu's row
/// union. The production collector is a thin read of these plus the two window bounds; the arithmetic
/// is what a placement decision depends on, so it is pinned here.
/// </summary>
public class ActivationChromeZonesTests
{
    [Fact]
    public void ActionMenuBounds_IsTheWindowSquareCentredOnTheMenuCentre()
    {
        // The window is a square of RingPlan.WindowSizeFor DIP centred on the clamped centre;
        // at ScaleX 2.0 its physical extent doubles with it. Mutation: divide by the scale instead of
        // multiplying (physical = DIP / scale) → the rect halves and this goes red.
        var center = new PhysicalPoint(1000, 800);
        double ringDip = 250;
        double expectedSizePx = RingPlan.WindowSizeFor(ringDip) * 2.0;

        var rect = ActivationChromeZones.ActionMenuBounds(center, ringDip, scale: 2.0);

        Check.That(rect.Width).IsCloseTo(expectedSizePx, 1e-9);
        Check.That(rect.Height).IsCloseTo(expectedSizePx, 1e-9);
        Check.That(rect.X).IsCloseTo(center.X - (expectedSizePx / 2), 1e-9);
        Check.That(rect.Y).IsCloseTo(center.Y - (expectedSizePx / 2), 1e-9);
    }

    [Fact]
    public void ModesMenuBounds_IsTheUnionOfTheReportedRows()
    {
        // The panel's interactive strip, not its outer frame: the union of the hit circles the window
        // reported. Mutation: use only the first row → the second row's overhang is outside the zone.
        var rows = new (string Id, PhysicalPoint Center, double Radius)[]
        {
            ("modes_left", new PhysicalPoint(1800, 300), 70),
            ("modes_right", new PhysicalPoint(2000, 300), 70),
        };

        var rect = ActivationChromeZones.ModesMenuBounds(rows);

        Check.That(rect).IsNotNull();
        Check.That(rect!.Value.X).IsCloseTo(1730, 1e-9);
        Check.That(rect.Value.Y).IsCloseTo(230, 1e-9);
        Check.That(rect.Value.Width).IsCloseTo(340, 1e-9);
        Check.That(rect.Value.Height).IsCloseTo(140, 1e-9);
    }

    [Fact]
    public void ModesMenuBounds_IsNullBeforeAnyPlacement()
    {
        Check.That(ActivationChromeZones.ModesMenuBounds(
            Array.Empty<(string, PhysicalPoint, double)>())).IsNull();
    }

    [Fact]
    public void ActionMenuGeometry_KeepsItsOwnButtonRadius_NotTheSharedOne()
    {
        // The zone depends on the menu geometry's button radius (its own constant since 2026-08-26);
        // a drift to LayoutConstants.SharedButtonRadius would shrink every action-menu zone.
        Check.That(ActionMenuGeometry.ButtonRadius).IsEqualTo(EyeCtrl.Core.LayoutConstants.ActionMenuButtonRadius);
        Check.That(ActionMenuGeometry.ButtonRadius).IsNotEqualTo(EyeCtrl.Core.LayoutConstants.SharedButtonRadius);
    }
}
