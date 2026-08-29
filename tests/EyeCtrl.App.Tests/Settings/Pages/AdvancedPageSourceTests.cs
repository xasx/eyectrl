namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class AdvancedPageSourceTests
{
    private static List<SettingsRowSpec> Rows() => new AdvancedPageSource(TestSettingsPageContext.Build())
        .Build().SelectMany(p => p.Rows).ToList();

    [Fact]
    public void Build_HasFifteenRows_ThreeSeparators_ResetLast()
    {
        var rows = Rows();
        Check.That(rows).HasSize(15);
        Check.That(rows.OfType<LabelSpec>()).HasSize(3);
        Check.That(rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("av_reset");
        Check.That(rows.Last()).IsInstanceOf<ButtonSpec>();
    }

    [Fact]
    public void Build_SectionOrder_BackdropDragScrollExternalDwell()
    {
        var ids = Rows().Where(r => r.Id.Length > 0).Select(r => r.Id).ToList();
        Check.That(ids.IndexOf("av_backdrop_enabled")).IsStrictlyLessThan(ids.IndexOf("av_drag_settle"));
        Check.That(ids.IndexOf("av_drag_settle")).IsStrictlyLessThan(ids.IndexOf("av_scroll_sens"));
        Check.That(ids.IndexOf("av_scroll_sens")).IsStrictlyLessThan(ids.IndexOf("av_ext_enabled"));
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
    public void Reset_CopiesTheFourGroups_LeavesOthersAlone()
    {
        var settings = new EyeCtrlSettings();
        settings.Backdrop.Opacity = 0.8;
        settings.Drag.SettleDwellMs = 1500;
        settings.Scroll.Sensitivity = 1.8;
        settings.ExternalDwell.CooldownMs = 900;
        settings.EyeCare.IntervalMinutes = 45; // must NOT be touched
        var source = new AdvancedPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single().OnActivate();
        var b = new BackdropSettings();
        var d = new DragSettings();
        var sc = new ScrollSettings();
        var e = new ExternalDwellSettings();
        Check.That(settings.Backdrop.Opacity).IsEqualTo(b.Opacity);
        Check.That(settings.Drag.SettleDwellMs).IsEqualTo(d.SettleDwellMs);
        Check.That(settings.Scroll.Sensitivity).IsEqualTo(sc.Sensitivity);
        Check.That(settings.ExternalDwell.CooldownMs).IsEqualTo(e.CooldownMs);
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(45);
    }
}
