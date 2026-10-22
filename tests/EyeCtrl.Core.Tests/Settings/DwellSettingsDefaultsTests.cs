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
        Check.That(dwell.DragReleaseButtonDwellMs).IsEqualTo(600);
        Check.That(dwell.DragRightEdgeButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void DwellPauseGraceMs_DefaultsToTheFixationGapAbortBound()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.DwellPauseGraceMs).IsEqualTo(500);
        Check.That(dwell.DwellPauseGraceMs).IsEqualTo(EyeCtrl.Core.Processing.FixationDetector.GapAbortMs);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void DwellPauseGraceMs_AcceptsTheRangeEnds(int value)
    {
        var dwell = new DwellSettings { DwellPauseGraceMs = value };

        Check.That(dwell.DwellPauseGraceMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void DwellPauseGraceMs_RejectsOutOfRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.DwellPauseGraceMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.DwellPauseGraceMs).IsEqualTo(500);
    }
}

public class DwellSettingsPauseGraceValidateTests
{
    [Fact]
    public void Validate_FlagsAnOutOfRangeGraceLoadedAroundTheGuard()
    {
        // Validate() is the second line of defence (a value that bypassed the Changing guard).
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField("_dwellPauseGraceMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, 1500);

        Check.That(dwell.Validate()).HasElementThatMatches(e => e.Contains(nameof(DwellSettings.DwellPauseGraceMs)));
    }
}
