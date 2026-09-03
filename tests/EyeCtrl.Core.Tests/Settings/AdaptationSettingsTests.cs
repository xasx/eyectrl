namespace EyeCtrl.Core.Tests.Settings;

using System;
using System.Linq;
using EyeCtrl.Core.Settings;

public class AdaptationSettingsTests
{
    [Fact]
    public void Defaults_AreOptInAndConservative()
    {
        var s = new AdaptationSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.Posture).IsEqualTo(AdaptationPosture.Conservative);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void EyeCtrlSettings_EnumeratesAdaptation()
    {
        var names = new EyeCtrlSettings().EnumerateSubSettings().Select(x => x.Name).ToList();
        Check.That(names).Contains("Adaptation");
    }

    [Fact]
    public void NewDefaults_AreAdaptationNeutralAndValid()
    {
        var s = new AdaptationSettings();

        // Per-knob enables default on.
        Check.That(s.AdaptDwell).IsTrue();
        Check.That(s.AdaptTolerance).IsTrue();
        Check.That(s.AdaptSmoothing).IsTrue();
        Check.That(s.AdaptGeometry).IsTrue();

        // Feedback toggles: cue on, suppression on, caregiver alerts off (opt-in, Phase 3).
        Check.That(s.ShowCue).IsTrue();
        Check.That(s.SuppressInSensitiveContexts).IsTrue();
        Check.That(s.CaregiverAlerts).IsFalse();

        // Band fractions match the §13.1 fixed envelope.
        Check.That(s.DwellShorterMaxFraction).IsEqualTo(0.40);
        Check.That(s.DwellLongerMaxFraction).IsEqualTo(0.50);
        Check.That(s.ToleranceLooserMaxFraction).IsEqualTo(0.50);
        Check.That(s.SmoothingMoreMaxFraction).IsEqualTo(0.50);
        Check.That(s.GeometryBiggerMaxFraction).IsEqualTo(0.50);

        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void BandFraction_OutOfRange_ThrowsFromSetterGuard()
    {
        // Invalid values can't be stored — the setter guard throws first — so we assert the guard, which
        // is the runtime protection. Validate() is the non-throwing aggregate counterpart, exercised via
        // the default-valid case above.
        var s = new AdaptationSettings();
        Check.ThatCode(() => s.ToleranceLooserMaxFraction = 1.5).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.DwellShorterMaxFraction = -0.1).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BandFraction_InRange_IsAccepted()
    {
        var s = new AdaptationSettings { ToleranceLooserMaxFraction = 0.25, DwellShorterMaxFraction = 0.0 };
        Check.That(s.ToleranceLooserMaxFraction).IsEqualTo(0.25);
        Check.That(s.DwellShorterMaxFraction).IsEqualTo(0.0);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void GeometryMaxMarginPx_DefaultsTo100_AndValidates()
    {
        // §13.1's feature-introduced geometry bound, landed by the geometry-routing slice: default
        // 100 physical px — generous enough that the default 0.50 fraction never binds on typical
        // baselines (~15–70 px margins), tight enough to bound overlap growth on large targets.
        var s = new AdaptationSettings();
        Check.That(s.GeometryMaxMarginPx).IsEqualTo(100.0);
        Check.That(s.Validate()).IsEmpty();
    }

    [Fact]
    public void GeometryMaxMarginPx_OutOfRange_ThrowsFromSetterGuard()
    {
        var s = new AdaptationSettings();
        Check.ThatCode(() => s.GeometryMaxMarginPx = -1.0).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => s.GeometryMaxMarginPx = 300.5).Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GeometryMaxMarginPx_AtTheBounds_IsAccepted()
    {
        var s = new AdaptationSettings { GeometryMaxMarginPx = 0.0 };
        s.GeometryMaxMarginPx = 300.0;
        Check.That(s.GeometryMaxMarginPx).IsEqualTo(300.0);
        Check.That(s.Validate()).IsEmpty();
    }
}
