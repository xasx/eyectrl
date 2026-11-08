namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Linq;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// R3 (rest activation): the „Aktivierung" choice and the per-style slider on the Verweilen page.
/// The combo binds <see cref="DwellSettings.ActivationStyle"/> with explicit codes (the enum
/// members, pinned here as literals together with their display so an enum reorder can never move
/// both sides in step); the slider below it is <c>d_rest_fill</c> (<see cref="DwellSettings.RestFillMs"/>)
/// under the default RestFill and today's <c>d_act</c> (<see cref="DwellSettings.ActivationButtonDwellMs"/>)
/// under OfferButton — one row at a time, swapped by the rebuild the combo's Set issues. The German
/// strings are pinned in <c>DwellActivationGermanStringsTests</c> (Localization).
/// </summary>
public class DwellActivationRowsTests
{
    private static ComboBoxSpec ActivationRow(SettingsPageContext ctx) =>
        new DwellPageSource(ctx).Build().SelectMany(p => p.Rows)
            .OfType<ComboBoxSpec>().Single(c => c.Id == "d_activation");

    [Fact]
    public void DefaultBuild_HasTheActivationChoice_WithBothOptions_AndEnumCodes()
    {
        var row = ActivationRow(TestSettingsPageContext.Build());

        Check.That(row.Display).ContainsExactly("Ring fills", "Button next to the ring");
        Check.That(row.Code).ContainsExactly("RestFill", "OfferButton");
        Check.That(row.Get()).IsEqualTo("RestFill");
        Check.WithCustomMessage("the style choice is part of the curated simple set (the owner tunes it in a sitting)")
            .That(row.Simple).IsTrue();
        Check.WithCustomMessage("every describable row carries a description")
            .That(row.Description).IsNotNull();
    }

    [Fact]
    public void DefaultBuild_RestFill_ShowsTheFillTimeSlider_BoundToRestFillMs()
    {
        var settings = new EyeCtrlSettings();
        var slider = new DwellPageSource(TestSettingsPageContext.Build(settings: settings)).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().Single(s => s.Id == "d_rest_fill");

        Check.That(slider.Min).IsEqualTo(200);
        Check.That(slider.Max).IsEqualTo(2000);
        Check.That(slider.Step).IsEqualTo(50);
        Check.That(slider.Get()).IsEqualTo(700);
        Check.WithCustomMessage("the fill-time slider is part of the curated simple set")
            .That(slider.Simple).IsTrue();

        slider.Set(850);
        Check.WithCustomMessage("the slider under RestFill must write RestFillMs — not the button dwell")
            .That(settings.Dwell.RestFillMs).IsEqualTo(850);
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(700);
    }

    [Fact]
    public void DefaultBuild_RestFill_HasNoButtonDwellSlider()
    {
        var ids = new DwellPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().Select(s => s.Id).ToList();

        Check.WithCustomMessage("under RestFill the button dwell row is replaced by the fill-time row")
            .That(ids).Not.Contains("d_act");
    }

    [Fact]
    public void OfferButtonBuild_ShowsTheButtonDwellSlider_BoundToActivationButtonDwellMs()
    {
        var settings = new EyeCtrlSettings
            { Dwell = { ActivationStyle = ActivationStyle.OfferButton } };
        var rows = new DwellPageSource(TestSettingsPageContext.Build(settings: settings)).Build()
            .SelectMany(p => p.Rows).ToList();
        var ids = rows.OfType<SliderSpec>().Select(s => s.Id).ToList();

        Check.WithCustomMessage("under OfferButton today's d_act row shows as before")
            .That(ids).Contains("d_act");
        Check.WithCustomMessage("under OfferButton the fill-time row is gone")
            .That(ids).Not.Contains("d_rest_fill");

        var dAct = rows.OfType<SliderSpec>().Single(s => s.Id == "d_act");
        dAct.Set(900);
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
        Check.WithCustomMessage("d_act must write the button dwell only")
            .That(settings.Dwell.RestFillMs).IsEqualTo(700);    }

    [Fact]
    public void TheChoice_SwitchesTheSetting_SavesAndRebuilds()
    {
        // The rebuild is the load-bearing half: the slider row below swaps with the style, and a
        // rebuild only ever fires from a COMPLETED dwell on one of the combo's options — never
        // while the user is dwelling on the choice itself (the Cursor tab's colour-row pattern).
        var settings = new EyeCtrlSettings();
        var rec = new RecordingSettingsPageContext(settings);
        var row = ActivationRow(rec.Context);

        row.Set("OfferButton");

        Check.That(settings.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.OfferButton);
        Check.That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(rec.RebuildContentCount).IsEqualTo(1);
        Check.That(row.Get()).IsEqualTo("OfferButton");
    }

    [Fact]
    public void TheChoice_CyclesBothCodes_ThroughItsOwnSetter()
    {
        var settings = new EyeCtrlSettings();
        var row = ActivationRow(TestSettingsPageContext.Build(settings: settings));

        row.Set("OfferButton");
        Check.That(settings.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.OfferButton);
        row.Set("RestFill");
        Check.That(settings.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);
    }

    [Fact]
    public void TheReset_RestoresTheStyleAndTheFillTime()
    {
        // Sampled fields, drift-guarded wholesale by DwellPageSourceResetDriftTests; this pins the
        // two R3 fields explicitly so a wrong DEFAULT (not just a forgotten reset line) also reds.
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
        settings.Dwell.RestFillMs = 1400;
        var rec = new RecordingSettingsPageContext(settings);
        var reset = new DwellPageSource(rec.Context).Build().SelectMany(p => p.Rows)
            .OfType<ButtonSpec>().Single(b => b.Id == "d_reset");

        reset.OnActivate();
        rec.LastModalOnYes!();

        var def = new DwellSettings();
        Check.That(settings.Dwell.ActivationStyle).IsEqualTo(def.ActivationStyle);
        Check.That(settings.Dwell.RestFillMs).IsEqualTo(def.RestFillMs);
    }
}
