namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// Wave D D4: the magnifier's commit rule had two owner-tuned properties with no row at all
/// (<c>stabilitySampleCount</c> 5 and <c>holdOutlierSkipLimit</c> 7/10 in the owner's file), so a
/// value changed by hand could not be restored by gaze. The rows bind those EXISTING properties —
/// no new setting, no migration, no default change — beside the hold tolerance they combine with.
/// </summary>
public class SelectionMagnifierRowsTests
{
    [Fact]
    public void TheTwoRows_AreAdvancedOnly_AndSitRightAfterTheHoldTolerance()
    {
        var rows = new SelectionPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var ids = rows.Select(r => r.Id).ToList();
        var hold = ids.IndexOf("s_hold");

        Check.That(hold).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the stability row is not directly beside the hold tolerance")
            .That(ids.IndexOf("s_stability")).IsEqualTo(hold + 1);
        Check.WithCustomMessage("the skip-outlier row is not directly beside the hold tolerance pair")
            .That(ids.IndexOf("s_skip")).IsEqualTo(hold + 2);
        Check.That(rows.Single(r => r.Id == "s_stability").Simple).IsFalse();
        Check.That(rows.Single(r => r.Id == "s_skip").Simple).IsFalse();
    }

    [Fact]
    public void EachRow_BindsItsOwnProperty_NotItsNeighbour()
    {
        var settings = new EyeCtrlSettings();
        settings.ManualSelection.StabilityWindowSize = 7;
        settings.ManualSelection.StabilitySampleCount = 5;
        settings.ManualSelection.HoldOutlierSkipLimit = 7;
        var sliders = Sliders(settings);

        Check.That(sliders["s_stability"].Get()).IsEqualTo(5);
        Check.That(sliders["s_skip"].Get()).IsEqualTo(7);

        sliders["s_stability"].Set(6);
        Check.That(settings.ManualSelection.StabilitySampleCount).IsEqualTo(6);
        Check.That(settings.ManualSelection.HoldOutlierSkipLimit).IsEqualTo(7); // untouched
        Check.That(settings.ManualSelection.HoldRadiusLogical)
            .IsEqualTo(new ManualSelectionSettings().HoldRadiusLogical);        // untouched

        sliders["s_skip"].Set(8);
        Check.That(settings.ManualSelection.HoldOutlierSkipLimit).IsEqualTo(8);
        Check.That(settings.ManualSelection.StabilitySampleCount).IsEqualTo(6); // untouched
    }

    [Fact]
    public void RaisingStability_LiftsTheSampleWindow_SoTheSaveValidationStaysGreen()
    {
        // K > M makes stability onset impossible, so Validate() rejects it — and SaveDebounced
        // only logs a rejection, silently losing the value on restart. The row reaches 30, so the
        // setter must keep the pair coherent (the same lift SettingsService applies at load).
        var settings = new EyeCtrlSettings();
        settings.ManualSelection.StabilityWindowSize = 7;
        var stability = Sliders(settings)["s_stability"];

        stability.Set(12);

        Check.That(settings.ManualSelection.StabilitySampleCount).IsEqualTo(12);
        Check.WithCustomMessage("K rose above M, so the settings file would fail validation")
            .That(settings.ManualSelection.StabilityWindowSize).IsEqualTo(12);
        Check.That(settings.Validate()).IsEmpty();
    }

    private static Dictionary<string, SliderSpec> Sliders(EyeCtrlSettings settings) =>
        new SelectionPageSource(TestSettingsPageContext.Build(settings)).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
}
