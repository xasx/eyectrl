namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using System.Linq;
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
    public void ComputeButtonCentres_GivesEachButtonItsOwnAscendingTarget()
    {
        // D29 (round 3 finding 2 lineage): each dwell button gets its own centre — the
        // field-round text rows shared one centre, and the detector's tie-break chose. The helper
        // takes ids only; the action carried in a row tuple is not part of its arithmetic.
        var ids = new[] { "a", "b", "c", "d" };

        var centres = ModesMenuWindow.ComputeButtonCentres(ids, windowLeft: 100, windowTop: 200, buttonSizeLogical: 120);

        Check.That(centres).HasSize(4);
        var xs = centres.Select(c => c.Centre.X).ToList();
        for (var i = 1; i < xs.Count; i++)
            Check.That(xs[i]).IsStrictlyGreaterThan(xs[i - 1]);
        var pitch = xs[1] - xs[0];
        for (var i = 2; i < xs.Count; i++)
            Check.That(xs[i] - xs[i - 1]).IsCloseTo(pitch, 1e-6);
        // Every centre shares the panel's vertical midline, and the radius is half the button.
        Check.That(centres.Select(c => c.Centre.Y).Distinct()).HasSize(1);
        Check.That(centres[0].Radius).IsCloseTo(60, 1e-6);
    }
}