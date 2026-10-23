namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
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
            .Contains(new[] { "d_tool", "d_key", "d_pred", "d_rad", "d_cooldown", "d_pause_grace" });
    }

    [Fact]
    public void Selection_HighlightColorUsesControlRadius()
    {
        var cp = new SelectionPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ColorPickerSpec>().Single();
        Check.That(cp.RadiusLogical).IsEqualTo(SettingsWindow.ControlBtnRadius);
    }

    [Fact]
    public void Selection_Build_HasFiveGazeIndicatorRows_RestartDescribed_ExceptOuter()
    {
        var sliders = new SelectionPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>()
            .Where(s => s.Id.StartsWith("s_gi_", StringComparison.Ordinal)).ToList();
        Check.That(sliders.Select(s => s.Id)).ContainsExactly(
            "s_gi_outer", "s_gi_dot", "s_gi_stroke", "s_gi_opacity", "s_gi_anim");
        // Four of the five are snapshot-once at startup; the outer diameter is re-read live on
        // every reset animation (OverlayMessageCoordinator), so its claim differs on purpose.
        Check.That(sliders.Where(s => s.Id != "s_gi_outer")
            .All(s => s.Description!.Contains("restart", StringComparison.Ordinal))).IsTrue();
        // NFluent 3.1.0 has no ICheck<string>.Contains(string, StringComparison) — a two-arg
        // call silently binds to the params-object[] enumerable overload (issue #124.4); the
        // one-arg form keeps NFluent's own failure diagnostic.
        Check.That(sliders.Single(s => s.Id == "s_gi_outer").Description!)
            .Contains("next selection");
        Check.That(sliders.Single(s => s.Id == "s_gi_outer").Get()).IsEqualTo(60);
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
    public void Toolbar_DockedEdgeComboHasCodeMapping()
    {
        // T03a: the saved values are the display strings, now pinned as explicit codes so T07 can
        // translate the display without changing what is persisted.
        var edge = new ToolbarPageSource(TestSettingsPageContext.Build()).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "t_edge");
        Check.That(edge.Code).ContainsExactly("Top", "Bottom", "Left", "Right");
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
            ["d_key"] = (300, 1500),
            ["d_pred"] = (300, 1500),
            ["d_rad"] = (10, 200),
            ["d_cooldown"] = (100, 5000),
            ["d_pause_grace"] = (0, 1000),
        };

        Check.That(sliders).HasSize(guards.Count);
        foreach (var (id, guard) in guards)
        {
            Check.That(sliders[id].Min).IsGreaterOrEqualThan(guard.Min);
            Check.That(sliders[id].Max).IsLessOrEqualThan(guard.Max);
        }
    }

    [Fact]
    public void KeyboardSliders_FloorAtTheFamilyMinimum_NotBelowTheBlinkCreditBound()
    {
        // The keyboard dwells are the family that can be lowered toward the 150 ms blink-credit
        // bounds (KeyboardViewModel.MaxContinuousSampleGapUs and DwellDetector.MaxCreditedSampleGapUs):
        // at a 150 ms dwell a single short blink completes a key. The family floor lives in
        // AdaptationDwellRanges.KeyboardMinDwellMs, is shared by the Core guard and the adaptive
        // routing clamp, and both sliders must offer nothing below it. The behavioural floor itself
        // is pinned where it can be measured (KeyboardEffectiveDwellTests in ViewModels,
        // KeyboardDwellFloorRoutingTests in App); this pins the UI range to the same constant so the
        // three cannot drift apart silently.
        var sliders = new DwellPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>()
            .ToDictionary(s => s.Id);

        Check.That(sliders["d_key"].Min).IsEqualTo(AdaptationDwellRanges.KeyboardMinDwellMs);
        Check.That(sliders["d_pred"].Min).IsEqualTo(AdaptationDwellRanges.KeyboardMinDwellMs);

        // Derive the lower bound from the setters rather than trusting the hand-restated guards
        // dictionary in Dwell_SliderSpans_StayInsideTheValidatedRanges: the slider minimum must be a
        // value DwellSettings ACCEPTS, so a raised guard cannot leave a slider offering a value whose
        // Set crashes the UI thread (DwellSlider.Adjust clamps to the spec Min then sets unguarded).
        var dwell = new DwellSettings();
        Check.ThatCode(() => dwell.KeyboardKeyDwellMs = (int)sliders["d_key"].Min).DoesNotThrow();
        Check.ThatCode(() => dwell.KeyboardPredictionDwellMs = (int)sliders["d_pred"].Min).DoesNotThrow();
    }
}
