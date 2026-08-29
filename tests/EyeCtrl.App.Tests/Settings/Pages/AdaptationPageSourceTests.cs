namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class AdaptationPageSourceTests
{
    [Fact]
    public void Build_HasFourteenRows_NoCaregiverRow()
    {
        var rows = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(14);
        Check.That(rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("ad_reset");
        Check.That(rows.Any(r => r.Id.Contains("caregiver", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void Build_PostureCombo_OffersTheThreeEnumValues()
    {
        var combo = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<ComboBoxSpec>().Single();
        Check.That(combo.Id).IsEqualTo("ad_posture");
        Check.That(combo.Display).ContainsExactly("Conservative", "Balanced", "Responsive");
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new AdaptationPageSource(TestSettingsPageContext.Build(settings));
        var rows = source.Build().SelectMany(p => p.Rows).ToList();
        var toggles = rows.OfType<ToggleSpec>().ToDictionary(t => t.Id);
        var sliders = rows.OfType<SliderSpec>().ToDictionary(s => s.Id);
        toggles["ad_enabled"].Set(true);
        Check.That(settings.Adaptation.Enabled).IsTrue();
        sliders["ad_frac_dwell_short"].Set(0.25);
        Check.That(settings.Adaptation.DwellShorterMaxFraction).IsEqualTo(0.25);
    }

    [Fact]
    public void Reset_CopiesAllThirteenFromDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        settings.Adaptation.Posture = AdaptationPosture.Responsive;
        settings.Adaptation.AdaptDwell = false;
        settings.Adaptation.AdaptTolerance = false;
        settings.Adaptation.AdaptSmoothing = false;
        settings.Adaptation.AdaptGeometry = false;
        settings.Adaptation.ShowCue = false;
        settings.Adaptation.SuppressInSensitiveContexts = false;
        settings.Adaptation.DwellShorterMaxFraction = 0.2;
        settings.Adaptation.DwellLongerMaxFraction = 0.3;
        settings.Adaptation.ToleranceLooserMaxFraction = 0.4;
        settings.Adaptation.SmoothingMoreMaxFraction = 0.6;
        settings.Adaptation.GeometryBiggerMaxFraction = 0.7;
        var source = new AdaptationPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single().OnActivate();
        var d = new AdaptationSettings();
        Check.That(settings.Adaptation.Enabled).IsEqualTo(d.Enabled);
        Check.That(settings.Adaptation.Posture).IsEqualTo(d.Posture);
        Check.That(settings.Adaptation.AdaptDwell).IsEqualTo(d.AdaptDwell);
        Check.That(settings.Adaptation.AdaptTolerance).IsEqualTo(d.AdaptTolerance);
        Check.That(settings.Adaptation.AdaptSmoothing).IsEqualTo(d.AdaptSmoothing);
        Check.That(settings.Adaptation.AdaptGeometry).IsEqualTo(d.AdaptGeometry);
        Check.That(settings.Adaptation.ShowCue).IsEqualTo(d.ShowCue);
        Check.That(settings.Adaptation.SuppressInSensitiveContexts).IsEqualTo(d.SuppressInSensitiveContexts);
        Check.That(settings.Adaptation.DwellShorterMaxFraction).IsEqualTo(d.DwellShorterMaxFraction);
        Check.That(settings.Adaptation.DwellLongerMaxFraction).IsEqualTo(d.DwellLongerMaxFraction);
        Check.That(settings.Adaptation.ToleranceLooserMaxFraction).IsEqualTo(d.ToleranceLooserMaxFraction);
        Check.That(settings.Adaptation.SmoothingMoreMaxFraction).IsEqualTo(d.SmoothingMoreMaxFraction);
        Check.That(settings.Adaptation.GeometryBiggerMaxFraction).IsEqualTo(d.GeometryBiggerMaxFraction);
    }
}
