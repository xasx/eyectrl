namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class DwellSettingsDefaultsTests
{
    [Fact]
    public void DefaultDwellTimes_AreRelaxed()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.FixationDetectionMs).IsEqualTo(300);
        Check.That(dwell.ActivationButtonDwellMs).IsEqualTo(700);
        Check.That(dwell.ActionMenuDwellMs).IsEqualTo(600);
        Check.That(dwell.ManualSelectionActionMenuDwellMs).IsEqualTo(600);
        Check.That(dwell.ToolbarButtonDwellMs).IsEqualTo(700);
        Check.That(dwell.KeyboardKeyDwellMs).IsEqualTo(450);
        Check.That(dwell.KeyboardPredictionDwellMs).IsEqualTo(500);
    }
}
