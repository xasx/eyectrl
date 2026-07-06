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
    /// Logical-px margin applied on top of the rightmost button's radius. Sized so the
    /// button's right edge never clips past the WPF <c>OverlayWindow</c>'s 0-px border.
    /// </summary>
    private const double RightEdgeWindowMarginLogical = 10;

    /// <summary>
    /// Anchor at the right screen edge in physical pixels. The returned centre is offset
    /// from the right edge by <paramref name="logicalInset"/> + <paramref name="logicalRadius"/>
    /// + <paramref name="logicalWindowMargin"/> logical px, so a button of that radius centred on
    /// the anchor fits fully on screen (its rightmost edge sits at the WPF window's right border).
    /// </summary>
    public static PhysicalPoint RightEdgeAnchor(
        double logicalInset,
        double logicalRadius,
        double logicalWindowMargin = RightEdgeWindowMarginLogical)
        => RightEdgeAnchor(
            logicalInset, logicalRadius, logicalWindowMargin,
            SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>
    /// Pure-arithmetic overload: given a logical screen size, returns the right-edge anchor in
    /// physical pixels. Exposed so tests can pin the geometry without spinning up WPF.
    /// </summary>
    internal static PhysicalPoint RightEdgeAnchor(
        double logicalInset,
        double logicalRadius,
        double logicalWindowMargin,
        double logicalScreenWidth,
        double logicalScreenHeight)
    {
        var rightOffsetLogical = logicalInset + logicalRadius + logicalWindowMargin;
        var w = DpiHelper.LogicalToPhysicalX(logicalScreenWidth);
        var h = DpiHelper.LogicalToPhysicalY(logicalScreenHeight);
        return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(rightOffsetLogical), h / 2.0);
    }
}
