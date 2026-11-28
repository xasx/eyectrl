namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// Wave D D4: the owner's stored 1.75× key size fell between the old 0.1 steps and the stepper
/// rounded to one decimal, so one − press wrote 1.7 and the chosen value could never come back by
/// gaze. The row now steps by 0.05 with two decimals; this pins that the stored value survives
/// − then + exactly, and the two-decimal display is pinned by
/// <c>DwellSliderFormatTests.TwoDecimals_RenderTheOwnersKeySize</c>.
/// </summary>
public class KeyboardKeySizeStepTests
{
    [Fact]
    public void KeySize_StepsByFiveHundredths_AndShowsTwoDecimals()
    {
        var slider = Slider(new EyeCtrlSettings());

        Check.That(slider.Step).IsEqualTo(0.05);
        Check.That(slider.Decimals).IsEqualTo(2);
        // The span is unchanged: this is a step/format change, not a range change.
        Check.That(slider.Min).IsEqualTo(0.5);
        Check.That(slider.Max).IsEqualTo(2.0);
    }

    [Fact]
    public void StoredOnePointSevenFive_SurvivesMinusThenPlus()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.KeySizeMultiplier = 1.75;
        var slider = Slider(settings);

        Check.WithCustomMessage("the stored 1.75 is not the value the row reads")
            .That(slider.Get()).IsEqualTo(1.75);

        // Exactly what DwellSlider.Adjust runs for one − press.
        slider.Set(Adjust(slider, -slider.Step));
        Check.WithCustomMessage("one − press lost the owner's 1.75")
            .That(settings.Keyboard.KeySizeMultiplier).IsEqualTo(1.70);

        // ... and one + press returns to it exactly.
        slider.Set(Adjust(slider, +slider.Step));
        Check.WithCustomMessage("one + press after one − did not return to 1.75")
            .That(settings.Keyboard.KeySizeMultiplier).IsEqualTo(1.75);
    }

    private static SliderSpec Slider(EyeCtrlSettings settings) =>
        new KeyboardPageSource(TestSettingsPageContext.Build(settings)).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().Single(s => s.Id == "k_size");

    private static double Adjust(SliderSpec s, double delta) =>
        Math.Round(Math.Clamp(s.Get() + delta, s.Min, s.Max), s.Decimals);
}

