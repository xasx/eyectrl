namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Shared screen-geometry helpers for the App layer. Returns physical-pixel anchors derived from
/// the primary screen via WPF <see cref="SystemParameters"/> (a single, DPI-aware source).
/// </summary>
internal static class ScreenGeometry
{
    /// <summary>
    /// Anchor at the right screen edge, vertically centred, inset by <paramref name="logicalInset"/>
    /// logical px from the right edge.
    /// </summary>
    public static PhysicalPoint RightEdgeAnchor(double logicalInset)
    {
        var w = DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
        var h = DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight);
        return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(logicalInset), h / 2.0);
    }
}
