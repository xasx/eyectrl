namespace EyeCtrl.Supervisor.Supervision;

using System;
using EyeCtrl.Core.Localization;

/// <summary>
/// What the setup window's "Start EyeCtrl at logon" row shows and does. The value is the parked
/// choice when there is one, otherwise the value on disk — never the Supervisor's in-memory
/// snapshot, which used to keep a toggled value that nothing had saved and show it as saved on
/// the next open. A choice goes to the App over the request channel; the Supervisor never writes
/// the key and never touches the Run key (its registrar would register the Supervisor's exe).
/// </summary>
internal sealed class StartOnBootSetupRow : IDisposable
{
    private readonly SettingDelivery<bool> _delivery;
    private bool _onDisk;

    /// <param name="delivery">The Supervisor's single StartOnBoot delivery; it outlives the window.</param>
    /// <param name="onDisk">Reads the value on disk (<see cref="StartOnBootTracker.Current"/>); read
    /// once here, when the window opens.</param>
    public StartOnBootSetupRow(SettingDelivery<bool> delivery, Func<bool> onDisk)
    {
        _delivery = delivery;
        _onDisk = onDisk();
        _delivery.Changed += OnDeliveryChanged;
    }

    /// <summary>Raised on the UI thread when what the row shows changes.</summary>
    public event Action? Changed;

    /// <summary>The value the toggle shows: the parked (or in-flight) choice, else the disk value.</summary>
    public bool Value => _delivery.Pending ?? _onDisk;

    /// <summary>Whether the row carries the parked note.</summary>
    public bool IsParked => _delivery.State == SettingDeliveryState.Parked && _delivery.Pending.HasValue;

    /// <summary>The toggle was dwelled: ask the App to save <paramref name="value"/>.</summary>
    public void Choose(bool value) => _delivery.Request(value);

    /// <summary>The note under the row; null when there is none.</summary>
    public string? Note(StringTable strings) =>
        IsParked ? strings.T("supervisor.setup.logon.parked") : null;

    /// <summary>The setup status line for the delivery's current state; null when it has nothing to say.</summary>
    public string? StatusText(StringTable strings) => _delivery.State switch
    {
        SettingDeliveryState.Sending => strings.T("supervisor.setup.logon.sending"),
        SettingDeliveryState.Saved => _onDisk
            ? strings.T("supervisor.setup.logon.saved-on")
            : strings.T("supervisor.setup.logon.saved-off"),
        SettingDeliveryState.Parked => strings.T("supervisor.setup.logon.parked"),
        SettingDeliveryState.Refused => strings.T("supervisor.setup.logon.refused"),
        SettingDeliveryState.Superseded => strings.T("supervisor.setup.logon.superseded"),
        _ => null,
    };

    private void OnDeliveryChanged()
    {
        // A save means the value is on disk now, and a superseded park means the disk holds a
        // newer choice: either way the row shows that value once the pending choice clears,
        // without a second disk read on the UI thread.
        if (_delivery.State == SettingDeliveryState.Saved && _delivery.LastSaved is { } saved)
            _onDisk = saved;
        else if (_delivery.State == SettingDeliveryState.Superseded && _delivery.SupersededBy is { } newer)
            _onDisk = newer;
        Changed?.Invoke();
    }

    public void Dispose() => _delivery.Changed -= OnDeliveryChanged;
}
