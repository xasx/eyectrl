namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class EyeCarePageSourceTests
{
    [Fact]
    public void Build_HasNineRows_LastIsReset()
    {
        var pages = new EyeCarePageSource(TestSettingsPageContext.Build()).Build();
        var rows = pages.SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(9);
        var reset = rows.OfType<ButtonSpec>().Single();
        Check.That(reset.Id).IsEqualTo("ec_reset");
        Check.That(reset.Kind).IsEqualTo(ButtonKind.Reset);
        Check.That(rows.Last()).IsSameReferenceAs(reset);
    }

    [Fact]
    public void Build_SliderRanges_MatchValidateRanges()
    {
        var rows = new EyeCarePageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
        Check.That(rows).HasSize(6);
        Check.That(rows["ec_interval"].Min).IsEqualTo(1);
        Check.That(rows["ec_interval"].Max).IsEqualTo(120);
        Check.That(rows["ec_break"].Min).IsEqualTo(5);
        Check.That(rows["ec_break"].Max).IsEqualTo(600);
        Check.That(rows["ec_exercise"].Min).IsEqualTo(10);
        Check.That(rows["ec_exercise"].Max).IsEqualTo(180);
        Check.That(rows["ec_snooze_banner"].Min).IsEqualTo(5);
        Check.That(rows["ec_snooze_banner"].Max).IsEqualTo(120);
        Check.That(rows["ec_snooze"].Min).IsEqualTo(1);
        Check.That(rows["ec_snooze"].Max).IsEqualTo(60);
        Check.That(rows["ec_fatigue_thresh"].Min).IsEqualTo(0.0);
        Check.That(rows["ec_fatigue_thresh"].Max).IsEqualTo(1.0);
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new EyeCarePageSource(TestSettingsPageContext.Build(settings));
        var toggles = source.Build().SelectMany(p => p.Rows).OfType<ToggleSpec>().ToDictionary(t => t.Id);
        toggles["ec_enabled"].Set(true);
        Check.That(settings.EyeCare.Enabled).IsTrue();
    }

    [Fact]
    public void Reset_CopiesAllEightFromDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.EyeCare.Enabled = true;
        settings.EyeCare.IntervalMinutes = 45;
        settings.EyeCare.NaturalBreakSeconds = 120;
        settings.EyeCare.ExerciseDurationSeconds = 90;
        settings.EyeCare.BannerAutoSnoozeSeconds = 30;
        settings.EyeCare.SnoozeMinutes = 10;
        settings.EyeCare.FatigueMonitorEnabled = true;
        settings.EyeCare.FatigueThreshold = 0.9;
        var source = new EyeCarePageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        var reset = source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single();
        reset.OnActivate();
        var d = new EyeCareSettings();
        Check.That(settings.EyeCare.Enabled).IsEqualTo(d.Enabled);
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(d.IntervalMinutes);
        Check.That(settings.EyeCare.NaturalBreakSeconds).IsEqualTo(d.NaturalBreakSeconds);
        Check.That(settings.EyeCare.ExerciseDurationSeconds).IsEqualTo(d.ExerciseDurationSeconds);
        Check.That(settings.EyeCare.BannerAutoSnoozeSeconds).IsEqualTo(d.BannerAutoSnoozeSeconds);
        Check.That(settings.EyeCare.SnoozeMinutes).IsEqualTo(d.SnoozeMinutes);
        Check.That(settings.EyeCare.FatigueMonitorEnabled).IsEqualTo(d.FatigueMonitorEnabled);
        Check.That(settings.EyeCare.FatigueThreshold).IsEqualTo(d.FatigueThreshold);
    }
}
