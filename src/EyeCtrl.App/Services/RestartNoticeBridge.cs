namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;

/// <summary>
/// The late-bound hand-off that carries the App-owned restart-notice card (constructed in
/// App.xaml.cs's startup, never in DI) to the lazily-resolved <see cref="GazeConsumerInventory"/>
/// (D17). The same problem <see cref="ToolbarLifecycleBridge"/> solves for the preview service's
/// suspension input: the inventory is built inside the DI container and resolved only on the first
/// adaptation enable, while the card is newed later in App startup — so the inventory cannot
/// constructor-inject it and instead reaches it through this holder.
/// <para>
/// App.xaml.cs attaches the live window immediately after building the <c>RestartNoticeHost</c>,
/// which is strictly before <c>AdaptationActivation</c> can first resolve the inventory (its
/// <c>StartRuntime</c> runs later in OnStartup), so a null window at use time is a wiring defect.
/// The source then degrades to empty (D14: a comfort feature's collision check tolerates a missing
/// optional source rather than crashing the enable edge) instead of throwing.
/// </para>
/// </summary>
internal sealed class RestartNoticeBridge
{
    private RestartNoticeWindow? _window;

    /// <summary>Hands over the live card window. Called once from App startup, after the
    /// <c>RestartNoticeHost</c> is built.</summary>
    public void Attach(RestartNoticeWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    /// <summary>The card's live dwell-target rects, or empty while no card is attached or none is
    /// showing — the D17 inventory's restart-notice source (the card's own detector snapshot, see
    /// <see cref="RestartNoticeWindow.LiveTargetRects"/>).</summary>
    public IReadOnlyList<PhysicalRect> LiveTargetRects() =>
        _window is { IsShowing: true } window
            ? window.LiveTargetRects()
            : Array.Empty<PhysicalRect>();
}
