namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Same contract as Dwell_SliderSpans_StayInsideTheValidatedRanges in
/// DwellSelectionKeyboardToolbarPageSourceTests, widened to every settings slider outside
/// the Dwell tab (which that test pins itself): every slider-bearing tab — Toolbar,
/// Selection, Keyboard, Calibration, Cursor, Quick Actions. A settings slider
/// must never offer a value its Core guard (OnXxxChanging or Validate) rejects. Two failure
/// modes make this load-bearing: setter guards throw on the UI thread (crash); Validate-only
/// guards like ToolbarSettings.Size throw inside SettingsService.Save, which SaveDebounced
/// swallows — the value applies live and is silently lost on restart. Guard bounds are
/// restated from the Core classes so a slider that leaves its guard fails loudly here; a Core
/// guard narrowed below a pinned bound is NOT caught (the pins bound the slider, not the guard).
/// s_grace (Grace Period) is deliberately not span-pinned: ManualSelectionSettings guards
/// neither the setter nor Validate for it — there is no guard to stay inside. Its row instead
/// pins the 0–2000 span the Core XML doc claims, so that doc cannot silently go false again.
/// The Quick Actions sliders live in the default List view — the GoTo sub-views are pickers
/// with no sliders — so a freshly constructed source reaches them without navigation.
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
        // s_grace has no Core guard (nothing to stay inside), but its XML doc claims the UI
        // offers 0–2000 — pin that claim, not a guard, so the doc cannot silently go false.
        Check.That(sliders["s_grace"].Min).IsEqualTo(0);
        Check.That(sliders["s_grace"].Max).IsEqualTo(2000);
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

    [Fact]
    public void Cursor_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new CursorPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(7);
        SpanInside(sliders, "c_smooth", 0.0, 1.0);
        SpanInside(sliders, "c_opacity", 0.0, 1.0);
        SpanInside(sliders, "c_thick", 1.0, 10.0);
        SpanInside(sliders, "c_gap", 0, 500);
        SpanInside(sliders, "c_size", 10, 500);
        SpanInside(sliders, "c_euro_cutoff", 0.1, 20.0);
        SpanInside(sliders, "c_euro_beta", 0.0, 5.0);
    }

    [Fact]
    public void QuickActions_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new QuickActionsPageSource(
            TestSettingsPageContext.Build(viewModel: QaTestSupport.MakeViewModel())));
        Check.That(sliders).HasSize(3);
        SpanInside(sliders, "qa_summon", 200, 2000);
        SpanInside(sliders, "qa_dismiss", 1000, 15000);
        SpanInside(sliders, "qa_columns", 2, 5);
    }

    [Fact]
    public void EyeCare_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new EyeCarePageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(6);
        SpanInside(sliders, "ec_interval", 1, 120);
        SpanInside(sliders, "ec_break", 5, 600);
        SpanInside(sliders, "ec_exercise", 10, 180);
        SpanInside(sliders, "ec_snooze_banner", 5, 120);
        SpanInside(sliders, "ec_snooze", 1, 60);
        SpanInside(sliders, "ec_fatigue_thresh", 0.0, 1.0);
    }

    [Fact]
    public void Adaptation_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new AdaptationPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(5);
        SpanInside(sliders, "ad_frac_dwell_short", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_dwell_long", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_tol_loose", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_smooth", 0.0, 1.0);
        SpanInside(sliders, "ad_frac_geom", 0.0, 1.0);
    }

    [Fact]
    public void Advanced_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new AdvancedPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(7);
        SpanInside(sliders, "av_backdrop_opacity", 0.1, 0.9);
        SpanInside(sliders, "av_drag_settle", 200, 2000);
        SpanInside(sliders, "av_drag_radius", 10, 200);
        SpanInside(sliders, "av_drag_opacity", 0.1, 1.0);
        SpanInside(sliders, "av_scroll_sens", 0.5, 2.0);
        SpanInside(sliders, "av_ext_dwell", 200, 3000);
        SpanInside(sliders, "av_ext_cooldown", 0, 5000);
    }

    private static Dictionary<string, SliderSpec> Sliders(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);

    private static void SpanInside(Dictionary<string, SliderSpec> sliders, string id, double min, double max)
    {
        Check.That(sliders).ContainsKey(id);
        Check.That(sliders[id].Min).IsGreaterOrEqualThan(min);
        Check.That(sliders[id].Max).IsLessOrEqualThan(max);
    }
}
