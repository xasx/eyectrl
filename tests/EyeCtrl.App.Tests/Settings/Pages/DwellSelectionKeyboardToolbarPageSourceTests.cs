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

    // Range-vs-guard: a dwell slider offering a value the Core setter rejects crashes on the UI
    // thread — DwellSlider.Adjust clamps to the spec's Min, then calls Set unguarded, and
    // App.xaml.cs deliberately lets dispatcher exceptions terminate the process. Guard bounds are
    // restated from DwellSettings.OnXxxChanging so moving either side fails loudly here.
    [Fact]
    public void Dwell_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = new DwellPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>()
            .ToDictionary(s => s.Id);

        var guards = new Dictionary<string, (double Min, double Max)>
        {
            ["d_fix"] = (100, 1000),
            ["d_act"] = (200, 2000),
            ["d_menu"] = (200, 2000),
            ["d_ManualSelection"] = (200, 2000),
            ["d_tool"] = (200, 2000),
            ["d_key"] = (100, 1500),
            ["d_pred"] = (100, 1500),
            ["d_rad"] = (10, 200),
            ["d_cooldown"] = (100, 5000),
        };

        Check.That(sliders).HasSize(guards.Count);
        foreach (var (id, guard) in guards)
        {
            Check.That(sliders[id].Min).IsGreaterOrEqualThan(guard.Min);
            Check.That(sliders[id].Max).IsLessOrEqualThan(guard.Max);
        }
    }
}
