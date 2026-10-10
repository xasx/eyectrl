namespace EyeCtrl.App.Tests.Settings.Pages;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
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
    public void ProbeButton_SendsTheMeasureOnlyRequest_AndIsAlwaysVisible()
    {
        // Deliberately visible without a diagnostics precondition: hiding it behind RecordRawGaze
        // would make a measurement session depend on a global toggle the gaze-only user cannot see.
        // And the request must be the measure-only one — starting the probe through "Calibrate Now"
        // would silently skip the transform.
        var messenger = new WeakReferenceMessenger();
        bool measureOnly = false, normal = false;
        messenger.Register<MeasureOnlyCalibrationRequestedMessage>(this, (_, _) => measureOnly = true);
        messenger.Register<CalibrationRequestedMessage>(this, (_, _) => normal = true);

        var pages = new CalibrationPageSource(TestSettingsPageContext.Build(messenger: messenger)).Build();
        var probe = pages.SelectMany(p => p.Rows).OfType<ButtonSpec>().Single(b => b.Id == "cal_probe");

        probe.OnActivate();

        Check.That(measureOnly).IsTrue();
        Check.That(normal).IsFalse();
    }

    [Fact]
    public void StatusText_None_WhenNoCalibration()
    {
        var src = new CalibrationPageSource(TestSettingsPageContext.Build());
        var label = src.Build()[1].Rows[0];
        Check.That(((LabelSpec)label).Text()).IsEqualTo("Last calibration: none");
    }

    /// <summary>
    /// T07: the calibration status and load-rejection sentences are <c>settings</c> keys now, so
    /// the table is the only place their English lives. Pinned as literals (the story's test
    /// convention) for the sentences no other test asserts through the real path — the four
    /// monitor-mismatch arms are pinned by <c>CalibrationServiceTests</c>, and "none" above.
    /// </summary>
    [Fact]
    public void CalibrationStatusAndRejectionEnglish_IsPinned()
    {
        var table = StringTable.EmbeddedEnglish;

        Check.That(table.T("settings.calibration.status.ok", "2026-10-08 14:03", 12.3, 96.4))
            .IsEqualTo("Last calibration: 2026-10-08 14:03  \u00b7  12.3 px (96.4%)");
        Check.That(table.T("settings.calibration.status.rejected", "Not applied \u2014 x."))
            .IsEqualTo("Last calibration: Not applied \u2014 x.");

        Check.That(table.T("settings.calibration.rejected.empty"))
            .IsEqualTo("Not applied \u2014 the saved calibration file is empty. Recalibrate to fix.");
        Check.That(table.T("settings.calibration.rejected.screen", 3840, 2400, 1920, 1080))
            .IsEqualTo("Not applied \u2014 saved for a 3840\u00d72400 screen, current is 1920\u00d71080. Recalibrate to fix.");
        Check.That(table.T("settings.calibration.rejected.malformed"))
            .IsEqualTo("Not applied \u2014 the saved calibration is malformed. Recalibrate to fix.");
        Check.That(table.T("settings.calibration.rejected.off_screen"))
            .IsEqualTo("Not applied \u2014 the saved calibration maps gaze off-screen (bad fit). Recalibrate to fix.");
        Check.That(table.T("settings.calibration.rejected.flips"))
            .IsEqualTo("Not applied \u2014 the saved calibration flips or amplifies gaze (bad fit). Recalibrate to fix.");
        Check.That(table.T("settings.calibration.rejected.unreadable"))
            .IsEqualTo("Not applied \u2014 the saved calibration could not be read. Recalibrate to fix.");
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
