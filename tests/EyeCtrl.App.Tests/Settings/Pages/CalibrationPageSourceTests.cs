namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
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
}
