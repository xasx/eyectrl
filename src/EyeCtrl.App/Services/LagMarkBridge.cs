namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

/// <summary>
/// The late-bound hand-off that carries the App-owned lag-mark button (constructed in
/// App.xaml.cs's startup inside the <c>DispatchLatencyProbe</c> guard, never in DI) to the
/// lazily-resolved <see cref="GazeConsumerInventory"/> (D17, second merge). The exact
/// <see cref="RestartNoticeBridge"/> problem and shape: the inventory is built inside the DI
/// container and resolved only on the first adaptation enable, while the button's window is newed
/// later in App startup — so the inventory cannot constructor-inject it and instead reaches it
/// through this holder.
/// <para>
/// App.xaml.cs attaches the live window immediately after building the <c>LagMarkHost</c>, which
/// is strictly before <c>AdaptationActivation</c> can first resolve the inventory (its
/// <c>StartRuntime</c> runs later in OnStartup), so a null window at use time is a wiring defect.
/// An ordinary (non-instrumented) session never constructs the host at all — the bridge then stays
/// empty and the source degrades to empty (D14: a comfort feature's collision check tolerates a
/// missing optional source rather than crashing the enable edge) instead of throwing.
/// </para>
/// </summary>
internal sealed class LagMarkBridge
{
    private LagMarkWindow? _window;

    /// <summary>Hands over the live button window. Called once from App startup, after the
    /// <c>LagMarkHost</c> is built (instrumented sessions only).</summary>
    public void Attach(LagMarkWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    /// <summary>The button's live dwell-target rects, or empty while no window is attached or the
    /// button is hidden — the D17 inventory's lag-mark source (the button's own detector
    /// snapshot, see <see cref="LagMarkWindow.LiveTargetRects"/>).</summary>
    public IReadOnlyList<PhysicalRect> LiveTargetRects() =>
        _window is { IsShowing: true } window
            ? window.LiveTargetRects()
            : Array.Empty<PhysicalRect>();
}
