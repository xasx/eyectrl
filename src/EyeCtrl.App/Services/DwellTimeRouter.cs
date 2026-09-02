namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Routes the momentary DwellTime fill to every registered dwell surface (design §13.3 dwell routing):
/// on every <see cref="EffectiveSettingsProvider.Changed"/> (a controller fill write — payloadless, so
/// the dwell map is re-applied even when another knob caused the event; the apply is idempotent) and on
/// every registration change (a late registrant gets the current values immediately). Per entry, the
/// map composes the fill against each target's own baseline and clamps to the surface's validated range;
/// with adaptation disabled or no fill held, <see cref="EffectiveSettingsProvider.Effective"/> returns
/// the baseline, so the identity map restores exact baselines — no teardown path exists or is needed.
/// Dwell time applies in place (no rebuild), so there is deliberately no §9.1 budget (the tolerance
/// precedent). UI-thread: provider fills are written on the dispatcher and surfaces mutate on the UI
/// thread; constructed eagerly at startup (it subscribes in its ctor — see AppBootstrapper).
/// </summary>
public sealed class DwellTimeRouter : IDisposable
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly IDwellDetectorRegistry _registry;

    public DwellTimeRouter(EffectiveSettingsProvider provider, IDwellDetectorRegistry registry)
    {
        _provider = provider;
        _registry = registry;
        _provider.Changed += ApplyAll;
        _registry.RegistrationChanged += ApplyAll;
        ApplyAll(); // entries registered before the router existed must not wait for the next event
                    // (review round 1, should-fix 9). Reentrancy-safe: Register/Unregister raise
                    // RegistrationChanged OUTSIDE the registry lock and Registrations snapshots on
                    // read, so a registration during ApplyAll neither deadlocks nor mutates the
                    // collection being iterated.
    }

    private void ApplyAll()
    {
        foreach (var registration in _registry.Registrations)
        {
            registration.Apply(MakeMap(registration));
        }
    }

    private Func<int, int> MakeMap(DwellRoutingRegistration registration) => baselineMs =>
    {
        double effective = _provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, baselineMs);
        return (int)Math.Clamp(Math.Round(effective), registration.MinDwellMs, registration.MaxDwellMs);
    };

    public void Dispose()
    {
        _provider.Changed -= ApplyAll;
        _registry.RegistrationChanged -= ApplyAll;
    }
}
