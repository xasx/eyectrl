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
    public void Build_RingStyleComboAndTheAppearanceSlidersItShares_AreAdjacentInFlatOrder()
    {
        // c_ring_style's setter no longer rewrites anything — it sets RingStyle and nothing
        // else, so there is no destructive write left to protect against. The grouping is
        // still the right layout: c_ring_style, c_opacity and c_thick all shape the same ring,
        // and controls that shape one thing belong next to each other on the page the user is
        // actually looking at, whether or not any of them writes another.
        //
        // CursorPageSource.PrefersAutoPagination is true, so the literal PageSpec
        // boundaries declared in Build() below are only advisory: SettingsWindow.
        // MaybeRepaginateByHeight (SettingsWindow.xaml.cs:767-781) flattens every page
        // with `pages.SelectMany(p => p.Rows)` and re-chunks that flat list by measured
        // height via SettingsAutoPagination.ChunkByHeight (SettingsAutoPagination.cs:
        // 27-51), a greedy sequential fill. Flat row order is the only placement
        // property that survives that re-chunk to the screen. Asserting per-page
        // membership would pass even if the rendered pagination split the combo from
        // the sliders, and would fail if the literal pages were merely regrouped while
        // the rows stayed adjacent — backwards on both counts. So this pins the three
        // rows as a contiguous run in flat order instead.
        //
        // Adjacency makes a split unlikely but cannot rule one out: a height-chunk
        // boundary can still fall between two adjacent rows if the budget runs out
        // exactly there. No test in this repo can exclude that; it needs a human
        // looking at the screen.
        var ids = new CursorPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).Select(r => r.Id).ToList();

        var ringStyleIndex = ids.IndexOf("c_ring_style");
        Check.That(ringStyleIndex).IsNotEqualTo(-1);
        Check.That(ids.Skip(ringStyleIndex).Take(3))
            .ContainsExactly("c_ring_style", "c_opacity", "c_thick");
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
        Check.That(sliders["c_euro_beta"].Max).IsEqualTo(60.0);
        // Reachability, which is what the old spec FAILED and no test caught: the pixel-unit beta
        // slider ran 0–5 by 0.1 while the correct value was ~0.01, so the only useful position on the
        // whole control was exactly 0 — tuning the shipped default was not possible through the UI at
        // all. Both One-Euro defaults must now be positions the slider can actually land on.
        var defaults = new CursorSettings();
        Check.That(defaults.OneEuroBetaPerDiagonal).IsEqualTo(20.0);
        Check.That(defaults.OneEuroMinCutoff).IsEqualTo(1.5);
        CheckReachable(sliders["c_euro_beta"], defaults.OneEuroBetaPerDiagonal);
        CheckReachable(sliders["c_euro_cutoff"], defaults.OneEuroMinCutoff);

        static void CheckReachable(SliderSpec slider, double value)
        {
            Check.That(value).IsGreaterOrEqualThan(slider.Min);
            Check.That(value).IsLessOrEqualThan(slider.Max);
            double steps = (value - slider.Min) / slider.Step;
            Check.That(Math.Abs(steps - Math.Round(steps))).IsStrictlyLessThan(1e-6);
        }
    }

    [Fact]
    public void Reset_RestoresCursorSettingsToTheirDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        settings.Cursor.SmoothingFactor = 0.75;
        settings.Cursor.RingStyle = CursorRingStyle.BiTonalSegments;
        settings.Cursor.CircleColor = "#123456";
        settings.Cursor.CircleOpacity = 0.2;
        settings.Cursor.CircleThickness = 6;
        settings.Cursor.MaxGapInterpolationMs = 400;
        settings.Cursor.PositionIndicatorStyle = PositionIndicatorStyle.Pin;
        settings.Cursor.CircleSize = 400;
        settings.Cursor.OneEuroMinCutoff = 12;
        settings.Cursor.OneEuroBetaPerDiagonal = 3;
        settings.Cursor.DeadbandRadiusPx = 11;
        settings.Cursor.DeadbandConfirmSamples = 4;
        var source = new CursorPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single(b => b.Id == "c_reset").OnActivate();
        var d = new CursorSettings();
        // Full census of the copy list the c_reset handler writes (issue #124.2): a dropped
        // copy line must fail here, not ship green.
        Check.That(settings.Cursor.FilterMode).IsEqualTo(d.FilterMode);
        Check.That(settings.Cursor.SmoothingFactor).IsEqualTo(d.SmoothingFactor);
        Check.That(settings.Cursor.RingStyle).IsEqualTo(d.RingStyle);
        Check.That(settings.Cursor.CircleColor).IsEqualTo(d.CircleColor);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(d.CircleOpacity);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(d.CircleThickness);
        Check.That(settings.Cursor.MaxGapInterpolationMs).IsEqualTo(d.MaxGapInterpolationMs);
        Check.That(settings.Cursor.PositionIndicatorStyle).IsEqualTo(d.PositionIndicatorStyle);
        Check.That(settings.Cursor.CircleSize).IsEqualTo(d.CircleSize);
        Check.That(settings.Cursor.OneEuroMinCutoff).IsEqualTo(d.OneEuroMinCutoff);
        Check.That(settings.Cursor.OneEuroBetaPerDiagonal).IsEqualTo(d.OneEuroBetaPerDiagonal);
        // The deadband knobs have no slider of their own, which makes them exactly the kind of setting
        // a reset quietly forgets.
        Check.That(settings.Cursor.DeadbandRadiusPx).IsEqualTo(d.DeadbandRadiusPx);
        Check.That(settings.Cursor.DeadbandConfirmSamples).IsEqualTo(d.DeadbandConfirmSamples);
    }

    [Fact]
    public void Build_RingStyleCombo_MapsDisplayOntoEnumNames()
    {
        var rows = new CursorPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var combo = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "c_ring_style");

        // Display and Code are positional, so BOTH are pinned here to a literal order rather
        // than to each other: production builds Code from this very same Enum.GetNames<...>()
        // call, so asserting Code against Enum.GetNames would compare production's own call
        // with itself and could never fail — an enum reorder moves both sides together. Only a
        // fixed literal order catches a reorder silently mislabelling every style button.
        Check.That(combo.Code).ContainsExactly(
            "WeightedSandwich", "BiTonalSegments", "AchromaticReticle");
        Check.That(combo.Display).ContainsExactly(
            "Weighted sandwich", "Bi-tonal segments", "Achromatic reticle");
        Check.That(combo.Description).IsNotNull();
    }

    [Fact]
    public void RingStyleSetter_ChangesOnlyRingStyle_LeavesThicknessAndOpacityUntouched()
    {
        // Full user control was the ask: the setter sets RingStyle and nothing else. Neither
        // thickness nor opacity is a safety knob the setter must protect — both are the
        // user's, exactly like colour and size. 6 and 0.2 are distinctive non-default starting
        // values so "unchanged" is a real assertion, not a coincidence of some default value.
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleThickness = 6;
        settings.Cursor.CircleOpacity = 0.2;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var combo = source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>()
            .Single(c => c.Id == "c_ring_style");

        combo.Set(nameof(CursorRingStyle.BiTonalSegments));

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.BiTonalSegments);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(6.0);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(0.2);
    }

    [Fact]
    public void Build_UnderAchromaticReticle_SwapsTheColourPickerForANote()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.RingStyle = CursorRingStyle.AchromaticReticle;
        var rows = new CursorPageSource(TestSettingsPageContext.Build(settings)).Build()
            .SelectMany(p => p.Rows).ToList();

        Check.That(rows.OfType<ColorPickerSpec>()).IsEmpty();
        var note = rows.OfType<LabelSpec>().Single();
        Check.That(note.Text()).IsEqualTo("Circle Color is not used by the Achromatic reticle ring style.");
    }

    [Theory]
    [InlineData(CursorRingStyle.WeightedSandwich)]
    [InlineData(CursorRingStyle.AchromaticReticle)]
    public void RingStyleSetter_ChangesOnlyRingStyle_ForEveryOtherStyleToo(CursorRingStyle style)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleThickness = 6;
        settings.Cursor.CircleOpacity = 0.2;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var combo = source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>()
            .Single(c => c.Id == "c_ring_style");

        // WeightedSandwich is the default CursorRingStyle, so selecting it is the
        // returning user's remediation gesture: dwelling an option that is already
        // selected. That behaviour is unchanged: the setter still runs (DwellComboBox wires
        // every option unconditionally, with no guard against the current selection) — it
        // simply now has nothing left to force, so thickness and opacity stay exactly as
        // the user left them.
        combo.Set(style.ToString());

        Check.That(settings.Cursor.RingStyle).IsEqualTo(style);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(6.0);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(0.2);
    }

    [Fact]
    public void Reset_RestoresTheRingStyle()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.RingStyle = CursorRingStyle.BiTonalSegments;
        // The default context DISCARDS the modal callback — TestSettingsPageContext.Build
        // defaults showModal to ((_, _) => { }) — so the confirmation must be run explicitly
        // or the reset copies nothing. Same pattern as Reset_RestoresCursorSettingsToTheirDefaults above.
        var source = new CursorPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        var reset = source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id == "c_reset");

        reset.OnActivate();

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }
}
