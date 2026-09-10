namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using System.Linq;
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
    public void ComputeRowCentres_GivesEachRowItsOwnAscendingTarget()
    {
        // Review PR #139 round 3 finding 2: the original loop had no index term, so all four
        // rows shared row 0's centre and the detector's tie-break picked the action.
        var defs = new (string Id, InteractionAction Action, string Label)[]
        {
            ("a", InteractionAction.LeftClick, "L"),
            ("b", InteractionAction.RightClick, "R"),
            ("c", InteractionAction.DoubleClick, "D"),
            ("d", InteractionAction.MiddleClick, "M"),
        };

        var centres = ModesMenuWindow.ComputeRowCentres(defs, windowLeft: 100, windowTop: 200);

        Check.That(centres).HasSize(4);
        var ys = centres.Select(c => c.Centre.Y).ToList();
        Check.That(ys).ContainsOnlyElementsThatMatch(y => y > 0);
        for (var i = 1; i < ys.Count; i++)
            Check.That(ys[i]).IsStrictlyGreaterThan(ys[i - 1]);
        // Uniform slot pitch (row height + spacing), identical for every row.
        var pitch = ys[1] - ys[0];
        for (var i = 2; i < ys.Count; i++)
            Check.That(ys[i] - ys[i - 1]).IsCloseTo(pitch, 1e-6);
    }
}