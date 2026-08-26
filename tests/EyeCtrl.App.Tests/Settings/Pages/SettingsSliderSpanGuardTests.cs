namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Same contract as Dwell_SliderSpans_StayInsideTheValidatedRanges in
/// DwellSelectionKeyboardToolbarPageSourceTests, widened to every other tab: a settings slider
/// must never offer a value its Core guard (OnXxxChanging or Validate) rejects. Two failure
/// modes make this load-bearing: setter guards throw on the UI thread (crash); Validate-only
/// guards like ToolbarSettings.Size throw inside SettingsService.Save, which SaveDebounced
/// swallows — the value applies live and is silently lost on restart. Guard bounds are
/// restated from the Core classes so moving either side fails loudly here.
/// s_grace (Grace Period) is deliberately not span-pinned: ManualSelectionSettings guards
/// neither the setter nor Validate for it — there is no guard to stay inside.
/// </summary>
public class SettingsSliderSpanGuardTests
{
    [Fact]
    public void Toolbar_SliderSpan_StaysInsideTheValidatedRange()
    {
        var sliders = Sliders(new ToolbarPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(1);
        SpanInside(sliders, "t_size", 40, 200);
    }

    [Fact]
    public void Selection_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new SelectionPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(5);
        Check.That(sliders.Keys).Contains(new[]
            { "s_rad", "s_mag", "s_grace", "s_highlightThickness", "s_snapRadius" });
        SpanInside(sliders, "s_rad", 50, 500);
        SpanInside(sliders, "s_mag", 1.5, 5.0);
        SpanInside(sliders, "s_highlightThickness", 1, 10);
        SpanInside(sliders, "s_snapRadius", 16, 96);
        // s_grace: no Core guard exists — nothing to pin (see the class doc).
    }

    [Fact]
    public void Keyboard_SliderSpan_StaysInsideTheValidatedRange()
    {
        var sliders = Sliders(new KeyboardPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(1);
        SpanInside(sliders, "k_size", 0.5, 2.0);
    }

    [Fact]
    public void Calibration_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new CalibrationPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(4);
        SpanInside(sliders, "cal_points", 3, 16);
        SpanInside(sliders, "cal_samples", 5, 100);
        SpanInside(sliders, "cal_hold", 200, 3000);
        SpanInside(sliders, "cal_settle", 0, 500);
    }

    private static Dictionary<string, SliderSpec> Sliders(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);

    private static void SpanInside(Dictionary<string, SliderSpec> sliders, string id, double min, double max)
    {
        Check.That(sliders[id].Min).IsGreaterOrEqualThan(min);
        Check.That(sliders[id].Max).IsLessOrEqualThan(max);
    }
}
