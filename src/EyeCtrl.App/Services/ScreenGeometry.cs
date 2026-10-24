namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Shared screen-geometry helpers for the App layer. Returns physical-pixel anchors derived from
/// the primary screen via WPF <see cref="SystemParameters"/> (a single, DPI-aware source).
/// </summary>
internal static class ScreenGeometry
{
    /// <summary>
    /// Logical-px margin the edge-button bar reserves around each button between the button
    /// circle and the overlay window border. Single source of truth: the right-edge anchor
    /// reserves this so the window never clips past the desktop, and
    /// <c>EdgeButtonBarWindow.ButtonMargin</c> uses the same value to size the window — the two
    /// must stay equal for the right edge to line up.
    /// </summary>
    internal const double EdgeButtonWindowMarginLogical = 10;

    /// <summary>
    /// Right-edge inset for every context whose exit lives on the shared screen-edge anchor:
    /// action menu, manual selection, drag, repeat and scroll. ONE value, so their out slots are
    /// the same absolute place on screen — the owner's "the out slot sits in the same place in
    /// each mode" check. The keyboard is the deliberate exception: its anchor tracks the
    /// keyboard's edge vertically and keeps its own inset.
    /// </summary>
    internal const double SharedRightEdgeInsetLogical = 30;

    /// <summary>
    /// The shared right-edge anchor for the exit-bearing modes, built through
    /// <see cref="RightEdgeAnchor"/> so every caller resolves the same numbers BY CONSTRUCTION.
    /// A per-caller inset is exactly how repeat and scroll once drifted 20 DIP inward from the
    /// action menu, drag and manual selection.
    /// </summary>
    internal static PhysicalPoint SharedRightEdgeExitAnchor()
        => RightEdgeAnchor(SharedRightEdgeInsetLogical, UIConstants.ButtonRadius);

    /// <summary>
    /// Anchor at the right screen edge in physical pixels. The returned centre is offset
    /// from the right edge by <paramref name="logicalInset"/> + <paramref name="logicalRadius"/>
    /// + <paramref name="logicalWindowMargin"/> logical px, so a button of that radius centred on
    /// the anchor fits fully on screen (its rightmost edge sits at the WPF window's right border).
    /// </summary>
    public static PhysicalPoint RightEdgeAnchor(
        double logicalInset,
        double logicalRadius,
        double logicalWindowMargin = EdgeButtonWindowMarginLogical)
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

    /// <summary>
    /// Anchor at the LEFT screen edge — the mirror of <see cref="RightEdgeAnchor"/>. The returned
    /// centre is offset from the left edge by <paramref name="logicalInset"/> +
    /// <paramref name="logicalRadius"/> + <paramref name="logicalWindowMargin"/> logical px.
    /// </summary>
    public static PhysicalPoint LeftEdgeAnchor(double logicalInset, double logicalRadius, double logicalWindowMargin = EdgeButtonWindowMarginLogical)
        => LeftEdgeAnchor(logicalInset, logicalRadius, logicalWindowMargin, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>Pure-arithmetic overload, exposed for the same reason as RightEdgeAnchor's.</summary>
    internal static PhysicalPoint LeftEdgeAnchor(double logicalInset, double logicalRadius, double logicalWindowMargin, double logicalScreenWidth, double logicalScreenHeight)
    {
        var leftOffsetLogical = logicalInset + logicalRadius + logicalWindowMargin;
        var h = DpiHelper.LogicalToPhysicalY(logicalScreenHeight);
        return new PhysicalPoint(DpiHelper.LogicalToPhysicalX(leftOffsetLogical), h / 2.0);
    }

    /// <summary>
    /// Anchor for the keyboard's edge-bar pair, tracking the keyboard's edge (spec ledger #15,
    /// mechanism B): keyboard Top → 25% of work-area height, Bottom → 75%, offset from the
    /// work-area TOP (not the screen top) so a taskbar shrinks the band the bar and the
    /// keyboard — which positions itself via <see cref="SystemParameters.WorkArea"/> — share.
    /// Same right-edge inset arithmetic as <see cref="RightEdgeAnchor"/>, only the vertical
    /// position differs.
    /// </summary>
    public static PhysicalPoint KeyboardEdgeBarAnchor(
        bool keyboardAtTop,
        double logicalRadius,
        double logicalWindowMargin = EdgeButtonWindowMarginLogical)
        => KeyboardEdgeBarAnchor(
            keyboardAtTop, logicalRadius, logicalWindowMargin,
            SystemParameters.PrimaryScreenWidth, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Height);

    /// <summary>Pure-arithmetic overload, exposed for the same reason as RightEdgeAnchor's.</summary>
    internal static PhysicalPoint KeyboardEdgeBarAnchor(
        bool keyboardAtTop,
        double logicalRadius,
        double logicalWindowMargin,
        double logicalScreenWidth,
        double logicalWorkAreaTop,
        double logicalWorkAreaHeight)
    {
        var fraction = keyboardAtTop ? 0.25 : 0.75;
        var w = DpiHelper.LogicalToPhysicalX(logicalScreenWidth);
        var y = DpiHelper.LogicalToPhysicalY(logicalWorkAreaTop)
                + DpiHelper.LogicalToPhysicalY(logicalWorkAreaHeight) * fraction;
        var rightOffsetLogical = KeyboardEdgeButtons.RightEdgeInsetLogical + logicalRadius + logicalWindowMargin;
        return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(rightOffsetLogical), y);
    }
}
