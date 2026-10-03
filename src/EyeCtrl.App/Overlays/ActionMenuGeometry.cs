namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Pure layout math for the radial action menu, kept separate from the window so it can be unit
/// tested without constructing WPF windows. The button positions the ViewModel produces are
/// ABSOLUTE physical-screen points; the window's canvas is WINDOW-RELATIVE, so buttons must be
/// converted with <see cref="ButtonCanvasCenter"/>. Placing them at absolute coordinates pushes
/// them off-canvas where they are clipped and invisible (regression fixed after commit 894ea34).
/// </summary>
internal static class ActionMenuGeometry
{
    /// <summary>
    /// Radius of an action-menu button, in logical (DIP) pixels. Reads the Core constant rather
    /// than restating it: this value and the ViewModel's dwell-target radius must agree, or the
    /// drawn button and the thing the gaze actually hits are different circles.
    /// </summary>
    internal const double ButtonRadius = LayoutConstants.ActionMenuButtonRadius;

    // WindowSize / WindowSizeMargin were deleted 2026-09-30 (T4): RingPlan.WindowSizeFor is the
    // one definition of the square the ring fits — the runtime window, the settings preview and
    // the fit scalar all read it there. The former copy here was the second site of the class of
    // two-site drift the ring plan exists to kill.

    /// <summary>
    /// Converts a button's absolute physical-screen position to a window-relative canvas point,
    /// given the menu's absolute center and the canvas center (which maps to that menu center).
    /// </summary>
    internal static Point ButtonCanvasCenter(PhysicalPoint itemPositionPhysical, PhysicalPoint menuCenterPhysical, Point canvasCenter)
        => new Point(
            canvasCenter.X + DpiHelper.PhysicalToLogicalX(itemPositionPhysical.X - menuCenterPhysical.X),
            canvasCenter.Y + DpiHelper.PhysicalToLogicalY(itemPositionPhysical.Y - menuCenterPhysical.Y));
}
