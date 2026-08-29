namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

public class CalibrationPageSourceTests
{
    [Fact]
    public void Calibration_TwoPages_StatusLabelThenRunAndClear()
    {
        var pages = new CalibrationPageSource(TestSettingsPageContext.Build()).Build();
        Check.That(pages).HasSize(2);
        Check.That(pages[1].Rows[0]).IsInstanceOf<LabelSpec>();
        var buttons = pages[1].Rows.OfType<ButtonSpec>().Select(b => b.Id);
        Check.That(buttons).Contains(new[] { "cal_run", "cal_clear" });
    }

    [Fact]
    public void StatusText_None_WhenNoCalibration()
    {
        var src = new CalibrationPageSource(TestSettingsPageContext.Build());
        var label = src.Build()[1].Rows[0];
        Check.That(((LabelSpec)label).Text()).IsEqualTo("Last calibration: none");
    }

    [Fact]
    public void Build_HasTargetColorPicker_AfterSettle()
    {
        var rows = new CalibrationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var cp = rows.OfType<ColorPickerSpec>().Single();
        Check.That(cp.Id).IsEqualTo("cal_color");
        Check.That(rows.IndexOf(cp)).IsEqualTo(4); // after the four sliders on page 1
        Check.That(cp.Get()).IsEqualTo(new CalibrationSettings().TargetColor);
        // ^ CalibrationSettings initializes TargetColor to SettingsDefaults.Accent; the internal
        // SettingsDefaults class itself is not visible from EyeCtrl.App.Tests (Core grants
        // InternalsVisibleTo only to EyeCtrl.Core.Tests), so the default is asserted through the
        // public settings object, the same pattern as SettingsDefaultsMatchTokensTests.
    }
}
