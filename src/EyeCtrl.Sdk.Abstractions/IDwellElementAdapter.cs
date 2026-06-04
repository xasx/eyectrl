namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

public enum DwellMode { Invoke, Focus }

/// <summary>
/// Per-element seam each framework head implements. The shared DwellClient reads
/// the screen rect + HWND to register, and calls Activate() when EyeCtrl reports a dwell.
/// The adapter owns its own DwellMode (so the client doesn't carry mode on the wire).
/// </summary>
public interface IDwellElementAdapter
{
    string Id { get; }
    DwellMode Mode { get; }
    long WindowHandle { get; }
    bool TryGetScreenRect(out SdkRect rect);
    void Activate();   // marshals to the UI thread internally, then invokes per Mode
}
