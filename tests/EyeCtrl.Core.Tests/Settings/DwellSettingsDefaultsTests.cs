namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class DwellSettingsDefaultsTests
{
    [Fact]
    public void DefaultDwellTimes_AreRelaxed()
    {
        var dwell = new DwellSettings();

        Assert.Equal(300, dwell.FixationDetectionMs);
        Assert.Equal(700, dwell.ActivationButtonDwellMs);
        Assert.Equal(600, dwell.ActionMenuDwellMs);
        Assert.Equal(600, dwell.ManualSelectionActionMenuDwellMs);
        Assert.Equal(700, dwell.ToolbarButtonDwellMs);
        Assert.Equal(450, dwell.KeyboardKeyDwellMs);
        Assert.Equal(500, dwell.KeyboardPredictionDwellMs);
    }
}
