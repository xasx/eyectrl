namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

[Collection("DpiHelper Tests (App)")]
public class RepeatModeControllerDefinitionTests
{
    public RepeatModeControllerDefinitionTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void CreateRepeatStopButtonDefinition_UsesFullButtonRadius()
    {
        var def = RepeatModeController.CreateRepeatStopButtonDefinition(
            dwellMs: 600, verticalOffset: 0);

        Check.That(def.Id).IsEqualTo("repeat-stop");
        Check.That(def.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
        Check.That(def.VerticalOffset).IsEqualTo(0.0);
        Check.That(def.DwellMs).IsEqualTo(600);
    }

    [Fact]
    public void CreateRepeatStopButtonDefinition_AppliesVerticalOffset()
    {
        var def = RepeatModeController.CreateRepeatStopButtonDefinition(
            dwellMs: 600, verticalOffset: 50.0);

        Check.That(def.VerticalOffset).IsEqualTo(50.0);
    }

    [Fact]
    public void IsInStopButtonZone_CoversTheButtonAndItsDwellMargin()
    {
        // Review PR #139 finding 3: the edge bar is click-through, so a tick aimed at the stop
        // button fires the locked click THROUGH it. Ticks into the button's zone (radius × the
        // 1.5 margin, covering centroid lag while the user dwells it) must be suppressed.
        var center = new PhysicalPoint(1850, 600);
        const double radius = 50;

        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(1850, 600), center, radius)).IsTrue();
        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(1900, 600), center, radius)).IsTrue();
        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(1850, 674), center, radius)).IsTrue();  // just inside 1.5×
        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(1850, 680), center, radius)).IsFalse(); // beyond 1.5×
        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(100, 100), center, radius)).IsFalse();
        Check.That(RepeatModeController.IsInStopButtonZone(new PhysicalPoint(1850, 600), center, 0)).IsFalse();      // degenerate radius
    }
}
