namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class DwellDetectorRegistryTests
{
    // Drift guard (the FixationRadius precedent): the range constants duplicate the DwellSettings
    // guards. Assigning min/max catches a NARROWED guard (the setter throws) and the equality checks
    // catch the CONSTANT drifting in either direction. A guard WIDENED beyond its constant is NOT
    // caught — that asymmetry is accepted; the constants, not the guards, are what routing reads.
    [Theory]
    [InlineData(nameof(DwellSettings.ActivationButtonDwellMs), 200, 2000, nameof(AdaptationDwellRanges.StandardMinDwellMs), nameof(AdaptationDwellRanges.StandardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.ToolbarButtonDwellMs), 200, 2000, nameof(AdaptationDwellRanges.StandardMinDwellMs), nameof(AdaptationDwellRanges.StandardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.KeyboardKeyDwellMs), 100, 1500, nameof(AdaptationDwellRanges.KeyboardMinDwellMs), nameof(AdaptationDwellRanges.KeyboardMaxDwellMs))]
    [InlineData(nameof(DwellSettings.KeyboardPredictionDwellMs), 100, 1500, nameof(AdaptationDwellRanges.KeyboardMinDwellMs), nameof(AdaptationDwellRanges.KeyboardMaxDwellMs))]
    public void Ranges_MatchTheirGuardFamilies(string propertyName, int min, int max, string minField, string maxField)
    {
        var settings = new DwellSettings();
        var property = typeof(DwellSettings).GetProperty(propertyName)!;
        property.SetValue(settings, min); // throws ArgumentOutOfRangeException below the guard floor
        property.SetValue(settings, max);
        Check.That(min).IsEqualTo((int)typeof(AdaptationDwellRanges).GetField(minField)!.GetValue(null)!);
        Check.That(max).IsEqualTo((int)typeof(AdaptationDwellRanges).GetField(maxField)!.GetValue(null)!);
    }

    [Fact]
    public void ExternalRange_MatchesExternalDwellSettingsGuard_AndEarnsItsCeiling()
    {
        // 3000, not the Standard 2000: a legal 2500 ms baseline identity-restored through Standard
        // would be truncated to 2000. Pin the truncation scenario through a real (disabled) provider —
        // Effective returns the baseline exactly, so the only variable is the clamp (round 1, nit 11).
        var s = new ExternalDwellSettings { DefaultDwellMs = AdaptationDwellRanges.ExternalMaxDwellMs };
        Check.That(s.DefaultDwellMs).IsEqualTo(3000);

        var provider = new EffectiveSettingsProvider(new AdaptationSettings()); // disabled ⇒ baseline
        double restore = provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 2500);
        Check.That(restore).IsEqualTo(2500.0);
        Check.That((int)Math.Clamp(Math.Round(restore),
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs)).IsEqualTo(2500);
        Check.That((int)Math.Clamp(Math.Round(restore),
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs)).IsEqualTo(2000);
    }

    [Fact]
    public void ExternalTighteningFill_ComposesToTheExternalCeiling_NotTheStandardUnion()
    {
        // Enabled-fill sibling of the identity-restore fact above: a legal 2500 ms External baseline under
        // the quarantined tightening fill (+0.50 × 2500 → 3750 intended) must compose to the External-family
        // ceiling 3000 = LONGER dwell (the safe direction). A provider union clamped to the Standard 2000
        // would INVERT the tightening response: 2500 → 2000 = SHORTER = easier accidental re-activation.
        var provider = new EffectiveSettingsProvider(new AdaptationSettings { Enabled = true });
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.0, tighteningFill: 1.0);

        double effective = provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 2500);

        Check.That(effective).IsEqualTo(3000.0); // intended 3750 clamped to the External ceiling, NOT 2000
        Check.That((int)Math.Clamp(Math.Round(effective),
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs)).IsEqualTo(3000);
    }

    [Fact]
    public void Register_AddsEntryAndRaisesChanged()
    {
        var registry = new DwellDetectorRegistry();
        int changed = 0;
        registry.RegistrationChanged += () => changed++;
        var owner = new object();

        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        Check.That(registry.Registrations).HasSize(1);
        Check.That(registry.Registrations[0].Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(changed).IsEqualTo(1);
    }

    [Fact]
    public void Register_SameOwner_ReplacesWithoutDuplicating()
    {
        var registry = new DwellDetectorRegistry();
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.EdgeButtonBar, 200, 2000));

        Check.That(registry.Registrations).HasSize(1);
        Check.That(registry.Registrations[0].Context).IsEqualTo(DwellContext.EdgeButtonBar);
    }

    [Fact]
    public void Unregister_RemovesAndRaises_UnknownOwnerIsNoOp()
    {
        var registry = new DwellDetectorRegistry();
        int changed = 0;
        registry.RegistrationChanged += () => changed++;
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));

        registry.Unregister(new object()); // unknown — no event
        Check.That(changed).IsEqualTo(1);
        registry.Unregister(owner);
        Check.That(registry.Registrations).IsEmpty();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void Registrations_ReturnsASnapshot_NotALiveView()
    {
        var registry = new DwellDetectorRegistry();
        var owner = new object();
        registry.Register(owner, DwellRoutingRegistration.ForDetector(
            new DwellDetector(), DwellContext.Toolbar, 200, 2000));
        var snapshot = registry.Registrations;

        registry.Unregister(owner);

        Check.That(snapshot).HasSize(1); // the snapshot still sees the old state
    }

    [Fact]
    public void ForDetector_AppliesMapThroughTheDetector()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(0, 0), radius: 10, dwellTimeMs: 1000));
        var registration = DwellRoutingRegistration.ForDetector(detector, DwellContext.Toolbar, 200, 2000);

        registration.Apply(ms => ms / 2);

        detector.Update(new PhysicalPoint(0, 0), 0);
        detector.Update(new PhysicalPoint(0, 0), 450_000);
        Check.That(detector.GetProgress("t")).IsCloseTo(0.9, 0.01); // 450/500, not 450/1000
    }
}
