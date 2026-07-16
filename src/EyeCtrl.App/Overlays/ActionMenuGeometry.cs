namespace EyeCtrl.App.Overlays;

using System.Windows;
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
    /// <summary>Radius of an action-menu button, in logical (DIP) pixels.</summary>
    internal const double ButtonRadius = 50;

    /// <summary>Extra margin (DIP) around the button ring when sizing the window.</summary>
    internal const int WindowSizeMargin = 20;

    /// <summary>Side length (DIP) of the square menu window that fits the button ring.</summary>
    internal static double WindowSize(double ringRadiusDip) => (ringRadiusDip + ButtonRadius) * 2 + WindowSizeMargin;

    /// <summary>
    /// Converts a button's absolute physical-screen position to a window-relative canvas point,
    /// given the menu's absolute center and the canvas center (which maps to that menu center).
    /// </summary>
    internal static Point ButtonCanvasCenter(PhysicalPoint itemPositionPhysical, PhysicalPoint menuCenterPhysical, Point canvasCenter)
        => new Point(
            canvasCenter.X + DpiHelper.PhysicalToLogicalX(itemPositionPhysical.X - menuCenterPhysical.X),
            canvasCenter.Y + DpiHelper.PhysicalToLogicalY(itemPositionPhysical.Y - menuCenterPhysical.Y));
}
