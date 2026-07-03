namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>How a dwell activation is delivered to the target element.</summary>
public enum DwellMode
{
    /// <summary>Invoke the element (UIA Invoke pattern, else a ButtonBase command).</summary>
    Invoke,

    /// <summary>Move keyboard focus to the element instead of invoking it.</summary>
    Focus,
}

/// <summary>
/// Per-element seam each framework head implements. The shared DwellClient reads
/// the screen rect + HWND to register, and calls Activate() when EyeCtrl reports a dwell.
/// The adapter owns its own DwellMode (so the client doesn't carry mode on the wire).
/// </summary>
public interface IDwellElementAdapter
{
    /// <summary>Stable id identifying this element to the dwell service.</summary>
    string Id { get; }

    /// <summary>How activation is delivered (invoke vs focus). Read live at activation time on the element's UI thread.</summary>
    DwellMode Mode { get; }

    /// <summary>Handle of the element's owning top-level window.</summary>
    long WindowHandle { get; }

    /// <summary>Tries to get the element's current bounds in physical screen pixels.</summary>
    bool TryGetScreenRect(out SdkRect rect);

    /// <summary>Activates the element, marshaling to the UI thread internally and invoking per <see cref="Mode"/>.</summary>
    void Activate();
}
