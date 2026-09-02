namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Application-level discovery of live dwell surfaces (design §13.3: "nothing can enumerate live
/// DwellDetectors" — this is that registry, shared with the geometry-routing slice). Owner-keyed:
/// re-registering replaces (idempotent for per-session owners like ManualSelectionDwellService);
/// <see cref="Unregister"/> on an unknown owner is a no-op. Raises <see cref="RegistrationChanged"/>
/// on every actual mutation so a router can apply the current effective values to a late registrant
/// immediately. Registrations are rare (construction / per-show), so a lock is free; the snapshot
/// copies on read so routers iterate a stable view.
/// </summary>
public interface IDwellDetectorRegistry
{
    /// <summary>Raised on every actual registration change (not for no-op unregisters).</summary>
    event Action? RegistrationChanged;
    /// <summary>Registers (or replaces) the entry owned by <paramref name="owner"/>.</summary>
    void Register(object owner, DwellRoutingRegistration registration);
    /// <summary>Removes the owner's entry; no-op (and no event) when absent.</summary>
    void Unregister(object owner);
    /// <summary>A stable snapshot of the live registrations.</summary>
    IReadOnlyList<DwellRoutingRegistration> Registrations { get; }
}

/// <summary>Default <see cref="IDwellDetectorRegistry"/> implementation.</summary>
public sealed class DwellDetectorRegistry : IDwellDetectorRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<object, DwellRoutingRegistration> _registrations = new();

    // NB: every public member needs its /// — EyeCtrl.Core.csproj escalates CS1591 (missing XML doc)
    // to an ERROR (review round 2, blocker). <inheritdoc /> pulls the interface docs.

    /// <inheritdoc />
    public event Action? RegistrationChanged;

    /// <inheritdoc />
    public void Register(object owner, DwellRoutingRegistration registration)
    {
        lock (_lock)
            _registrations[owner] = registration;
        RegistrationChanged?.Invoke();
    }

    /// <inheritdoc />
    public void Unregister(object owner)
    {
        bool removed;
        lock (_lock)
            removed = _registrations.Remove(owner);
        if (removed)
            RegistrationChanged?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<DwellRoutingRegistration> Registrations
    {
        get { lock (_lock) return _registrations.Values.ToArray(); }
    }
}
