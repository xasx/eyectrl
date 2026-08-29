namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

public class CursorPageSourceTests
{
    [Fact]
    public void Build_TwoPages_FilterThenReset()
    {
        var source = new CursorPageSource(TestSettingsPageContext.Build());
        var pages = source.Build();

        Check.That(pages).HasSize(2);
        Check.That(pages[0].Rows[0]).IsInstanceOf<ComboBoxSpec>();
        Check.That(((ComboBoxSpec)pages[0].Rows[0]).Id).IsEqualTo("c_filter");

        var reset = pages[1].Rows.OfType<ButtonSpec>().Single();
        Check.That(reset.Id).IsEqualTo("c_reset");
        Check.That(reset.Kind).IsEqualTo(ButtonKind.Reset);
    }

    [Fact]
    public void Build_ColorPickerUsesSwatchRadius()
    {
        var source = new CursorPageSource(TestSettingsPageContext.Build());
        var cp = source.Build().SelectMany(p => p.Rows).OfType<ColorPickerSpec>().Single();
        Check.That(cp.RadiusLogical).IsEqualTo(SettingsWindow.ColorSwatchRadius);
    }

    [Fact]
    public void Build_GettersReadLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.SmoothingFactor = 0.75;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var smooth = source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>()
            .Single(s => s.Id == "c_smooth");
        Check.That(smooth.Get()).IsEqualTo(0.75);
    }

    [Fact]
    public void Build_HasAdvancedRows_ResetStillLast()
    {
        var rows = new CursorPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        Check.That(rows.OfType<SliderSpec>().Count(s => s.Id.StartsWith("c_", StringComparison.Ordinal)))
            .IsEqualTo(7);
        Check.That(rows.Last()).IsInstanceOf<ButtonSpec>();
        var combo = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "c_pos_style");
        Check.That(combo.Display).ContainsExactly("Crosshair", "Pin");
    }

    [Fact]
    public void Build_GapAndEuroSliders_MatchValidateRanges()
    {
        var sliders = new CursorPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
        Check.That(sliders["c_gap"].Min).IsEqualTo(0);
        Check.That(sliders["c_gap"].Max).IsEqualTo(500);
        Check.That(sliders["c_size"].Min).IsEqualTo(10);
        Check.That(sliders["c_size"].Max).IsEqualTo(500);
        Check.That(sliders["c_euro_cutoff"].Min).IsEqualTo(0.1);
        Check.That(sliders["c_euro_cutoff"].Max).IsEqualTo(20.0);
        Check.That(sliders["c_euro_beta"].Min).IsEqualTo(0.0);
        Check.That(sliders["c_euro_beta"].Max).IsEqualTo(5.0);
    }

    [Fact]
    public void Reset_CopiesAllTenProperties()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.MaxGapInterpolationMs = 400;
        settings.Cursor.PositionIndicatorStyle = PositionIndicatorStyle.Pin;
        settings.Cursor.CircleSize = 400;
        settings.Cursor.OneEuroMinCutoff = 12;
        settings.Cursor.OneEuroBeta = 3;
        var source = new CursorPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single(b => b.Id == "c_reset").OnActivate();
        var d = new CursorSettings();
        Check.That(settings.Cursor.MaxGapInterpolationMs).IsEqualTo(d.MaxGapInterpolationMs);
        Check.That(settings.Cursor.PositionIndicatorStyle).IsEqualTo(d.PositionIndicatorStyle);
        Check.That(settings.Cursor.CircleSize).IsEqualTo(d.CircleSize);
        Check.That(settings.Cursor.OneEuroMinCutoff).IsEqualTo(d.OneEuroMinCutoff);
        Check.That(settings.Cursor.OneEuroBeta).IsEqualTo(d.OneEuroBeta);
    }
}
