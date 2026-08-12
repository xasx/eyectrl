namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class DwellSelectionKeyboardToolbarPageSourceTests
{
    // No local Ctx() — use the shared TestSettingsPageContext.Build() introduced in Task 2.
    [Fact]
    public void Dwell_TwoPagesWithCooldownAndReset()
    {
        var pages = new DwellPageSource(TestSettingsPageContext.Build()).Build();
        Check.That(pages).HasSize(2);
        Check.That(pages[1].Rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("d_reset");
        Check.That(pages[1].Rows.OfType<SliderSpec>().Select(s => s.Id))
            .Contains(new[] { "d_tool", "d_key", "d_pred", "d_rad", "d_cooldown" });
    }

    [Fact]
    public void Selection_HighlightColorUsesControlRadius()
    {
        var cp = new SelectionPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ColorPickerSpec>().Single();
        Check.That(cp.RadiusLogical).IsEqualTo(SettingsWindow.ControlBtnRadius);
    }

    [Fact]
    public void Keyboard_LanguageComboHasCodeMapping()
    {
        var lang = new KeyboardPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "k_lang");
        Check.That(lang.Code).IsNotNull();
        Check.That(lang.Code!).Contains(new[] { "en", "de" });
    }

    [Fact]
    public void Toolbar_DockedEdgeComboHasNullCode()
    {
        var edge = new ToolbarPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "t_edge");
        Check.That(edge.Code).IsNull();
    }
}
