namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class DwellTimeRouterTests
{
    // A capture fake standing in for a surface: records the applied map, answers like a real target.
    private sealed class CaptureEntry
    {
        public Func<int, int>? Applied;
        public DwellRoutingRegistration AsRegistration(DwellContext context = DwellContext.Toolbar,
            int min = AdaptationDwellRanges.StandardMinDwellMs, int max = AdaptationDwellRanges.StandardMaxDwellMs) =>
            new(context, min, max, map => Applied = map);
    }

    private static (DwellTimeRouter Router, EffectiveSettingsProvider Provider, DwellDetectorRegistry Registry) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        return (new DwellTimeRouter(provider, registry), provider, registry);
    }

    [Fact]
    public void ProviderChanged_AppliesComposedMap_WithTheEntryRange()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        // Full forgiving fill: 700 − 0.40×700 = 420; the map must compose AND clamp to the entry range.
        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(700)).IsEqualTo(420);
        Check.That(entry.Applied(300)).IsEqualTo(200); // 300 − 0.40×300 = 180, below the Standard floor → clamped (the provider's union floor is 100)
    }

    [Fact]
    public void ClearedFills_IdentityMap_RestoresExactBaseline()
    {
        var (router, provider, registry) = Make();
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        provider.ClearMomentaryFills();

        Check.That(entry.Applied!(700)).IsEqualTo(700);
    }

    [Fact]
    public void LateRegistration_AppliesImmediately()
    {
        var (router, provider, registry) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        var entry = new CaptureEntry();

        registry.Register(new object(), entry.AsRegistration()); // AFTER the fill exists

        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(700)).IsEqualTo(420);
    }

    [Fact]
    public void Constructor_AppliesToEntriesRegisteredBeforeConstruction()
    {
        // Resolve-order insurance (whole-branch review): every other fact constructs the router BEFORE
        // registering, so the RegistrationChanged subscription masks the ctor ApplyAll() — deleting that
        // call would fail nothing. This fact registers first; the map must arrive with no event at all.
        // (Make() cannot be used: it constructs the router first.)
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration()); // BEFORE any router exists

        using var router = new DwellTimeRouter(provider, registry);

        // Full forgiving fill applied at construction: 700 − 0.40×700 = 420 (no subsequent event fired).
        Check.That(entry.Applied).IsNotNull();
        Check.That(entry.Applied!(700)).IsEqualTo(420);
    }

    [Fact]
    public void DisabledProvider_IdentityMap()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = false } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var registry = new DwellDetectorRegistry();
        using var router = new DwellTimeRouter(provider, registry);
        var entry = new CaptureEntry();
        registry.Register(new object(), entry.AsRegistration());

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        Check.That(entry.Applied!(700)).IsEqualTo(700); // gated in Effective → baseline
    }
}
