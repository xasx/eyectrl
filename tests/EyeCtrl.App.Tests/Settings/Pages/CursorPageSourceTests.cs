namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
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
}
