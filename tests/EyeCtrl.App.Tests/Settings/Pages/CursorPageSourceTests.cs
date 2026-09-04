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
    public void Build_RingStyleCombo_SharesAPageWithTheSlidersItsSetterRewrites()
    {
        // c_ring_style's setter overwrites CircleThickness (c_thick) and CircleOpacity
        // (c_opacity) as a preset write. That write must land on the page the user is
        // looking at, or it is a destructive, invisible edit for a gaze user — the very
        // defect this feature exists to fix. This is a deliberate placement decision
        // (the combo moved from page 1 to page 2), not an accident of construction
        // order, so assert per-page membership: a SelectMany over every page's rows
        // would pass no matter which page the combo or the sliders ended up on, and
        // would silently stop protecting the adjacency this test exists to pin.
        var pages = new CursorPageSource(TestSettingsPageContext.Build()).Build();
        var ringStylePage = pages.Single(p => p.Rows.OfType<ComboBoxSpec>().Any(c => c.Id == "c_ring_style"));

        Check.That(ringStylePage.Rows.OfType<SliderSpec>().Select(s => s.Id))
            .Contains("c_thick", "c_opacity");
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
    public void RingStyleSetter_WritesThePresetWeights()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleThickness = 1;
        settings.Cursor.CircleOpacity = 0.2;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var combo = source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>()
            .Single(c => c.Id == "c_ring_style");

        combo.Set(nameof(CursorRingStyle.BiTonalSegments));

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.BiTonalSegments);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(5.0);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(1.0);
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
    public void RingStyleSetter_WritesTheDefaultPresetWeights(CursorRingStyle style)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleThickness = 1;
        settings.Cursor.CircleOpacity = 0.2;
        var source = new CursorPageSource(TestSettingsPageContext.Build(settings));
        var combo = source.Build().SelectMany(p => p.Rows).OfType<ComboBoxSpec>()
            .Single(c => c.Id == "c_ring_style");

        // WeightedSandwich is the default CursorRingStyle, so selecting it is the
        // returning user's remediation gesture: dwelling an option that is already
        // selected. The setter must still run and write the preset weights, or the
        // (2, 0.6) legibility defect this feature exists to fix is never corrected.
        combo.Set(style.ToString());

        Check.That(settings.Cursor.RingStyle).IsEqualTo(style);
        Check.That(settings.Cursor.CircleThickness).IsEqualTo(3.0);
        Check.That(settings.Cursor.CircleOpacity).IsEqualTo(1.0);
    }

    [Fact]
    public void Reset_RestoresTheRingStyle()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.RingStyle = CursorRingStyle.BiTonalSegments;
        // The default context DISCARDS the modal callback — TestSettingsPageContext.Build
        // defaults showModal to ((_, _) => { }) — so the confirmation must be run explicitly
        // or the reset copies nothing. Same pattern as Reset_CopiesAllTenProperties above.
        var source = new CursorPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        var reset = source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id == "c_reset");

        reset.OnActivate();

        Check.That(settings.Cursor.RingStyle).IsEqualTo(CursorRingStyle.WeightedSandwich);
    }
}
