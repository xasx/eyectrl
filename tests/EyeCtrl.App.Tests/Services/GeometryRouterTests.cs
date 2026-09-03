namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class GeometryRouterTests
{
    // A capture fake standing in for a surface: records the applied geometry map.
    private sealed class CaptureEntry
    {
        public Func<double, double>? Applied;
        public DwellRoutingRegistration AsRegistration(DwellContext context = DwellContext.Toolbar) =>
            new(context, AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs,
                _ => { }, map => Applied = map);
    }

    private static (GeometryRouter Router, EffectiveSettingsProvider Provider, DwellDetectorRegistry Registry) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        return (new GeometryRouter(provider, registry, settings), provider, registry);
    }

    [Fact]
    public void ProviderChanged_AppliesMarginMap_CappedAtTheSetting()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        // Full forgiving fill at the default 0.50 fraction: 60 → 90 (margin 30, under the cap)…
        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
        // …and 700 → 800, NOT 1050 — the §13.1 absolute bound caps the margin at 100 px.
        Check.That(entry.Applied(700)).IsEqualTo(800.0);
    }

    [Fact]
    public void ClearedFills_IdentityMap_RestoresExactGeometry()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);

        provider.ClearMomentaryFills();

        Check.That(entry.Applied!(60)).IsEqualTo(60.0); // margin 0 ⇒ as-registered geometry
    }

    [Fact]
    public void LateRegistration_AppliesImmediately()
    {
        var (router, provider, registry) = Make();
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);
        var entry = new CaptureEntry();

        registry.Register(new object(), entry.AsRegistration()); // AFTER the fill exists

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
    }

    [Fact]
    public void Constructor_AppliesToEntriesRegisteredBeforeConstruction()
    {
        // Resolve-order insurance: every other fact constructs the router BEFORE registering, so the
        // RegistrationChanged subscription masks the ctor ApplyAll() — deleting that call would fail
        // nothing. This fact registers first; the map must arrive with no event at all.
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration()); // BEFORE any router exists

        using var router = new GeometryRouter(provider, registry, settings);

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(60)).IsEqualTo(90.0);
    }

    [Fact]
    public void DisabledProvider_IdentityMap()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = false } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        using var router = new GeometryRouter(provider, registry, settings);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, 1.0, 0.0);

        Check.That(entry.Applied!(60)).IsEqualTo(60.0); // gated in Effective → baseline
    }
}
