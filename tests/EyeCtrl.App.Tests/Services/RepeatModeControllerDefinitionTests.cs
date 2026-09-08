namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Coordinators;
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
}
