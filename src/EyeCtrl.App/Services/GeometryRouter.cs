namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Routes the momentary TargetGeometry fill to every registered dwell surface (design §13.3 geometry
/// routing): on every <see cref="EffectiveSettingsProvider.Changed"/> (payloadless — the geometry map
/// is re-applied even when another knob caused the event; the apply is idempotent) and on every
/// registration change (late registrants get the current values immediately). Per entry, the map
/// composes the fill against each target's base hit extent (radius, or the smaller rect half-extent)
/// and returns an ABSOLUTE extent — the detector converts it to the signed margin
/// <see cref="DwellDetector.UpdateTargetGeometry"/> consumes (§13.3's unit-mismatch resolution). The
/// §13.1 feature-introduced absolute bound is applied HERE — margin clamped to [0,
/// <see cref="AdaptationSettings.GeometryMaxMarginPx"/>] — the consumer-side-clamp precedent
/// (smoothing per-filter in GazeEngine, dwell per-surface in DwellTimeRouter). With adaptation
/// disabled or no fill held, <see cref="EffectiveSettingsProvider.Effective"/> returns the baseline,
/// so the margin is 0 and the exact as-registered geometry is restored — no teardown path exists or
/// is needed. Hit areas only: no EyeCtrl-drawn element changes size (v1 scoping, §13.3); the one
/// visible element coupled to hit geometry is External's dwell-feedback overlay, which sizes itself
/// from the routed rect (§8.9's visible⇒hit direction, by construction). In-place and idempotent,
/// so there is deliberately no §9.1 budget (the tolerance
/// precedent). A cap EDIT raises <c>Changed</c> since the Phase-2 learned-offset slice (the provider
/// subscribes to the composition-relevant settings edits), so a lowered cap re-applies immediately.
/// UI-thread: constructed eagerly at startup (it subscribes and applies in its ctor — resolved in
/// App.xaml.cs's eager-resolve block).
/// </summary>
public sealed class GeometryRouter : IDisposable
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly IDwellDetectorRegistry _registry;
    private readonly AdaptationSettings _adaptation;

    public GeometryRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry,
        EyeCtrlSettings settings)
    {
        _provider = provider;
        _registry = registry;
        _adaptation = settings.Adaptation;
        _provider.Changed += ApplyAll;
        _registry.RegistrationChanged += ApplyAll;
        ApplyAll(); // entries registered before the router existed must not wait for the next event.
                    // Reentrancy-safe: Register/Unregister raise RegistrationChanged OUTSIDE the
                    // registry lock and Registrations snapshots on read (the DwellTimeRouter analysis).
    }

    private void ApplyAll()
    {
        foreach (var registration in _registry.Registrations)
            registration.ApplyGeometry(MakeMap());
    }

    private Func<double, double> MakeMap() => baseExtent =>
    {
        double effective = _provider.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, baseExtent);
        double margin = Math.Clamp(effective - baseExtent, 0.0, _adaptation.GeometryMaxMarginPx);
        return baseExtent + margin;
    };

    public void Dispose()
    {
        _provider.Changed -= ApplyAll;
        _registry.RegistrationChanged -= ApplyAll;
    }
}
