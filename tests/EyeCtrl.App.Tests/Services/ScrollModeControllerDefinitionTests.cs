namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

[Collection("DpiHelper Tests (App)")]
public class ScrollModeControllerDefinitionTests
{
    public ScrollModeControllerDefinitionTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void CreateScrollStopButtonDefinition_UsesFullButtonRadius()
    {
        var def = ScrollModeController.CreateScrollStopButtonDefinition(
            messenger: null!, dwellMs: 600, verticalOffset: 0);

        Check.That(def.Id).IsEqualTo("scroll-stop");
        Check.That(def.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
        Check.That(def.VerticalOffset).IsEqualTo(0.0);
        Check.That(def.DwellMs).IsEqualTo(600);
    }

    [Fact]
    public void CreateScrollStopButtonDefinition_AppliesVerticalOffset()
    {
        var def = ScrollModeController.CreateScrollStopButtonDefinition(
            messenger: null!, dwellMs: 600, verticalOffset: 50.0);

        Check.That(def.VerticalOffset).IsEqualTo(50.0);
    }
}
