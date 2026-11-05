namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Input;

/// <summary>
/// C9 F2 fixup-02: the one composition-root wire that switches the show-time band verification on
/// in production — the process token's UIAccess (from the cached read that also writes C10's token
/// line) and the overlay manager's order repair. Extracted from <c>App.OnStartup</c> so both
/// assignments are driven by a unit test; <c>TheApp_CallsTheBandStartupWireExactlyOnce</c> pins the
/// single production call. Neither assignment is exercised by the per-window gates, so without
/// this the feature can be deleted entirely while every band test stays green (review-02 N1 and
/// ring review-02 F2).
/// </summary>
internal static class OverlayBandStartup
{
    /// <summary>
    /// Wires the process-wide switch: whether a topmost insert can reach ZBID_UIACCESS, and the
    /// manager method a non-ring fallback calls to restore the visible order.
    /// </summary>
    internal static void WireOverlayBand(SelfTokenFacts selfToken, IOverlayManager overlayManager)
    {
        ArgumentNullException.ThrowIfNull(overlayManager);

        OverlayWindow.ProcessHasUiAccess = selfToken.HasUIAccess;
        OverlayWindow.ProcessReassertOverlayOrder = overlayManager.ReassertOverlayOrder;
    }
}
