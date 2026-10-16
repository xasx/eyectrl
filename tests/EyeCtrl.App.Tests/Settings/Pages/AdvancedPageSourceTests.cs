namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core;
using EyeCtrl.Core.Settings;

public class AdvancedPageSourceTests
{
    private static List<SettingsRowSpec> Rows() => new AdvancedPageSource(TestSettingsPageContext.Build())
        .Build().SelectMany(p => p.Rows).ToList();

    [Fact]
    public void Build_HasSeventeenRows_FourSeparators_ResetLast()
    {
        var rows = Rows();
        Check.That(rows).HasSize(17);
        Check.That(rows.OfType<SeparatorSpec>()).HasSize(4);
        Check.That(rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("av_reset");
        Check.That(rows.Last()).IsInstanceOf<ButtonSpec>();
    }

    [Fact]
    public void Build_SectionOrder_BackdropDragScrollRepeatExternalDwell()
    {
        var ids = Rows().Where(r => r.Id.Length > 0).Select(r => r.Id).ToList();
        Check.That(ids.IndexOf("av_backdrop_enabled")).IsStrictlyLessThan(ids.IndexOf("av_drag_settle"));
        Check.That(ids.IndexOf("av_drag_settle")).IsStrictlyLessThan(ids.IndexOf("av_scroll_sens"));
        Check.That(ids.IndexOf("av_scroll_sens")).IsStrictlyLessThan(ids.IndexOf("av_repeat_dwell"));
        Check.That(ids.IndexOf("av_repeat_dwell")).IsStrictlyLessThan(ids.IndexOf("av_ext_enabled"));
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new AdvancedPageSource(TestSettingsPageContext.Build(settings));
        var sliders = source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
        sliders["av_scroll_sens"].Set(1.5);
        Check.That(settings.Scroll.Sensitivity).IsEqualTo(1.5);
    }

    [Fact]
    public void BackdropOpacitySlider_WritesBothWidenedEnds_Live()
    {
        // The slider spans the widened range from the shared constants; driving it to each end must
        // reach the setting. A slider widened past the setter guard throws on the UI thread right
        // here (the mutant: slider widened, guard not), which is exactly what this pins.
        var settings = new EyeCtrlSettings();
        var source = new AdvancedPageSource(TestSettingsPageContext.Build(settings));
        var slider = source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>()
            .Single(s => s.Id == "av_backdrop_opacity");

        slider.Set(BackdropSettings.OpacityMin);
        Check.That(settings.Backdrop.Opacity).IsEqualTo(0.0);
        slider.Set(BackdropSettings.OpacityMax);
        Check.That(settings.Backdrop.Opacity).IsEqualTo(0.95);
    }

    [Fact]
    public void Reset_CopiesAllTwelveFromDefaults_LeavesEyeCareAlone()
    {
        var settings = new EyeCtrlSettings();
        settings.Backdrop.Enabled = false;
        settings.Backdrop.Type = BackdropType.SolidDarken;
        settings.Backdrop.Opacity = 0.8;
        settings.Drag.SettleDwellMs = 1500;
        settings.Drag.SettleRadiusPixels = 100;
        settings.Drag.TrailColor = "#123456";
        settings.Drag.TrailOpacity = 0.4;
        settings.Scroll.Sensitivity = 1.8;
        settings.Repeat.DwellMs = 1500;
        settings.ExternalDwell.Enabled = false;
        settings.ExternalDwell.DefaultDwellMs = 2000;
        settings.ExternalDwell.CooldownMs = 900;
        settings.EyeCare.IntervalMinutes = 45; // must NOT be touched
        var source = new AdvancedPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single().OnActivate();
        var b = new BackdropSettings();
        var d = new DragSettings();
        var sc = new ScrollSettings();
        var r = new RepeatSettings();
        var e = new ExternalDwellSettings();
        // Full census of the copy list the av_reset handler writes (issue #124.3): every
        // property a dropped copy line could strand perturbed is asserted back to default.
        Check.That(settings.Backdrop.Enabled).IsEqualTo(b.Enabled);
        Check.That(settings.Backdrop.Type).IsEqualTo(b.Type);
        Check.That(settings.Backdrop.Opacity).IsEqualTo(b.Opacity);
        Check.That(settings.Drag.SettleDwellMs).IsEqualTo(d.SettleDwellMs);
        Check.That(settings.Drag.SettleRadiusPixels).IsEqualTo(d.SettleRadiusPixels);
        Check.That(settings.Drag.TrailColor).IsEqualTo(d.TrailColor);
        Check.That(settings.Drag.TrailOpacity).IsEqualTo(d.TrailOpacity);
        Check.That(settings.Scroll.Sensitivity).IsEqualTo(sc.Sensitivity);
        Check.That(settings.Repeat.DwellMs).IsEqualTo(r.DwellMs);
        Check.That(settings.ExternalDwell.Enabled).IsEqualTo(e.Enabled);
        Check.That(settings.ExternalDwell.DefaultDwellMs).IsEqualTo(e.DefaultDwellMs);
        Check.That(settings.ExternalDwell.CooldownMs).IsEqualTo(e.CooldownMs);
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(45);
    }
}
