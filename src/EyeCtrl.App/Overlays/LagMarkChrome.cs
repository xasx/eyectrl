namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The chrome the T0(a) lag-mark button keeps clear of (review 01 F2): the zones the activation
/// offer already avoids (the shown keyboard, a docked toolbar, an open menu) plus the visible
/// edge-button bars.
///
/// <para>
/// The edge bars are deliberately NOT joined to <c>ActivationChromeZones</c> — the offers handle
/// them their own way — but the button carries its own dwell target, and the keyboard's Close/Flip
/// pair tracks the keyboard's edge at 75 % of the work area's height
/// (<see cref="ScreenGeometry.KeyboardEdgeBarAnchor"/>), reaching into the button's resting place
/// (measured: Flip's hit circle 38 DIP into it). One gaze would then be counted by both dwell
/// detectors — a spurious mark, or a keyboard flip from a mis-aimed mark.
/// </para>
/// </summary>
internal static class LagMarkChrome
{
    /// <summary>
    /// All the button's keep-clear rectangles, in DIP: the offer zones plus the visible edge bars.
    /// Called from the App wiring's <c>keepClear</c> lambda, so it reads the live windows and view
    /// models exactly as the offers' collector does.
    /// </summary>
    /// <param name="context">The coordination context the App wiring already holds.</param>
    /// <returns>The rectangles in DIP.</returns>
    internal static IReadOnlyList<Rect> CollectFromProduction(EventCoordinationContext context) =>
    [
        .. RestartNoticePlacement.ToDip(ActivationChromeZones.CollectFromProduction(context)),
        .. VisibleEdgeBarsInDip(context),
    ];

    /// <summary>
    /// The visible edge bars' window bounds in DIP: the right-edge bar (which carries the keyboard's
    /// Close/Flip pair while the keyboard is up) and the action-menu modifier bar. A bar with no
    /// buttons or none up contributes nothing.
    /// </summary>
    /// <param name="context">The coordination context.</param>
    /// <returns>One rectangle per visible bar, in DIP.</returns>
    internal static IEnumerable<Rect> VisibleEdgeBarsInDip(EventCoordinationContext context)
    {
        foreach (var bar in new[] { context.OverlayManager.EdgeButtonBar, context.OverlayManager.ActionMenuModBar })
        {
            if (bar.EdgeBarVm is not { } vm) continue;
            if (EdgeBarBounds(vm) is { } bounds) yield return bounds;
        }
    }

    /// <summary>
    /// One bar's bounds in DIP — the same arithmetic its window sizes itself with in
    /// <c>EdgeButtonBarWindow.SyncButtons</c>: each button's seed centre (physical px) converted to
    /// DIP and grown by its radius plus the window margin
    /// (<see cref="ScreenGeometry.EdgeButtonWindowMarginLogical"/>). Null when the bar is down or
    /// has no placed buttons.
    /// </summary>
    /// <param name="vm">The bar's view model.</param>
    /// <returns>The bar's window rectangle in DIP, or null.</returns>
    internal static Rect? EdgeBarBounds(EdgeButtonBarViewModel vm)
    {
        if (!vm.IsVisible) return null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var state in vm.Buttons)
        {
            if (!vm.SeedCenters.TryGetValue(state.Id, out var centre)) continue;
            var x = DpiHelper.PhysicalToLogicalX(centre.X);
            var y = DpiHelper.PhysicalToLogicalY(centre.Y);
            var extent = state.RadiusLogical + ScreenGeometry.EdgeButtonWindowMarginLogical;
            minX = Math.Min(minX, x - extent); maxX = Math.Max(maxX, x + extent);
            minY = Math.Min(minY, y - extent); maxY = Math.Max(maxY, y + extent);
            any = true;
        }

        return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : null;
    }
}
