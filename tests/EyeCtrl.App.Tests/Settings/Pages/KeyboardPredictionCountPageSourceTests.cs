namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Max Predictions row on the Keyboard tab (first slice of prediction controls, gap #22).
/// New file: the combined Dwell/Selection/Keyboard/Toolbar page-source test file is under active
/// edit on feat/keyboard-action-edge-toggle.
/// </summary>
public class KeyboardPredictionCountPageSourceTests
{
    [Fact]
    public void Build_HasMaxPredictionsSlider_AfterWordPredictionToggle()
    {
        var ctx = TestSettingsPageContext.Build();
        var rows = new KeyboardPageSource(ctx).Build()
            .SelectMany(p => p.Rows).ToList();
        var slider = rows.OfType<SliderSpec>().Single(s => s.Id == "k_pred_count");
        Check.That(slider.Min).IsEqualTo(1.0);
        Check.That(slider.Max).IsEqualTo(5.0);
        Check.That(slider.Step).IsEqualTo(1.0);
        Check.That(slider.Get()).IsEqualTo(5.0);
        Check.That(slider.Description).IsNotNull();

        var predToggle = rows.OfType<ToggleSpec>().Single(t => t.Id == "k_pred");
        Check.That(rows.IndexOf(slider)).IsEqualTo(rows.IndexOf(predToggle) + 1);

        slider.Set(2.0);
        Check.That(ctx.Settings.Keyboard.MaxSuggestions).IsEqualTo(2);
    }

    [Fact]
    public void ResetKeyboard_RestoresMaxSuggestions()
    {
        // The default test ShowModal is a no-op that discards the confirm action; auto-confirm
        // so the reset handler actually runs (same pattern as the other reset-button tests).
        var ctx = TestSettingsPageContext.Build(showModal: (_, act) => act());
        ctx.Settings.Keyboard.MaxSuggestions = 1;

        var rows = new KeyboardPageSource(ctx).Build()
            .SelectMany(p => p.Rows).ToList();
        var reset = rows.OfType<ButtonSpec>().Single(b => b.Id == "k_reset");
        reset.OnActivate(); // TestSettingsPageContext's ShowModal runs the action synchronously

        Check.That(ctx.Settings.Keyboard.MaxSuggestions).IsEqualTo(new KeyboardSettings().MaxSuggestions);
    }
}
