namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The eye-care surfaces' placement arithmetic, shared by the banner's rings and the exercise's
/// Skip exit (B7 fixup-01 F2/F3). Pure and DIP-based, so the headless tests drive every toolbar
/// dock and both DPIs without a display.
///
/// <para>
/// "Clear of the toolbar" means clear of the toolbar's real occupied band, whose thickness is the
/// button row (the <c>ToolbarWindow</c> Canvas: button size + one <c>ToolbarBand.ButtonMargin</c>
/// on each side) plus the strip's 4 DIP padding on each side — the same band the keyboard yields
/// by, read from the window rather than re-invented. The toolbar is docked Top, Bottom, Left or
/// Right; only a docked toolbar occupies an edge at all.
/// </para>
/// </summary>
internal static class EyeCarePlacement
{
    /// <summary>The banner strip's height in DIP (EyeCareBannerWindow.xaml's Border Height).</summary>
    internal const double MessageStripHeightDip = 96;

    /// <summary>Gap between the strip or the toolbar band and the banner rings, in DIP.</summary>
    internal const double RingEdgeGapDip = 8;

    /// <summary>DIP from the screen's side edge to the banner ring panel.</summary>
    internal const double RingSideInsetDip = 20;

    /// <summary>The earliest a banner ring may start: below the message strip with a visible gap.</summary>
    internal const double MinimumRingTopDip = MessageStripHeightDip + RingEdgeGapDip;

    /// <summary>DIP from the screen's side edge to the exercise Skip disc's edge.</summary>
    internal const double SkipEdgeInsetDip = 8;

    /// <summary>The Skip disc's centre distance from its edge: its radius plus
    /// <see cref="SkipEdgeInsetDip"/> (B7 fixup-01's W − 58 / 58 DIP).</summary>
    internal const double SkipCenterInsetDip = 50 + SkipEdgeInsetDip;

    /// <summary>The toolbar band's thickness in DIP — the drawn row the keyboard yields by.</summary>
    internal static double ToolbarBandExtentDip(double buttonSizeDip) =>
        buttonSizeDip + 2 * ToolbarBand.ButtonMargin;

    /// <summary>The toolbar window's thickness in DIP: its band plus the strip's padding on each
    /// side (<see cref="ToolbarWindow.BandPaddingDip"/>).</summary>
    internal static double ToolbarWindowThicknessDip(double buttonSizeDip) =>
        ToolbarBandExtentDip(buttonSizeDip) + 2 * ToolbarWindow.BandPaddingDip;

    /// <summary>Whether the toolbar is docked at <paramref name="edge"/> (case-insensitive, as
    /// <c>ToolbarSettings.DockedEdge</c> stores PascalCase but the placement code compares loosely).</summary>
    internal static bool DockedAt(ToolbarSettings toolbar, string edge) =>
        toolbar.IsDocked && string.Equals(toolbar.DockedEdge, edge, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the toolbar occupies the right edge — the Side the Skip and the banner
    /// rings move away from.</summary>
    internal static bool DockedAtRight(ToolbarSettings toolbar) => DockedAt(toolbar, "Right");

    /// <summary>
    /// The banner rings' top edge in DIP. A top-docked toolbar is a ceiling: the rings start below
    /// its real window thickness plus the standard gap. Every other dock leaves the top free, so
    /// the rings only clear the message strip.
    /// </summary>
    internal static double RingsTopDip(ToolbarSettings toolbar) =>
        Math.Max(
            MinimumRingTopDip,
            DockedAt(toolbar, "Top")
                ? ToolbarWindowThicknessDip(toolbar.Size) + RingEdgeGapDip
                : 0);

    /// <summary>
    /// The exercise Skip's hit-circle centre in physical pixels: mid-right edge, vertically
    /// centred — or mid-left when the toolbar is docked Right, so the exit never sits on the
    /// toolbar's own buttons. Mid-edge, never a corner (the ticket's rule), and away from the
    /// bottom-centre band the keyboard, the B4 slot, the caregiver alert and the restart notice
    /// share.
    /// </summary>
    /// <param name="screenDipWidth">The screen's width in DIP.</param>
    /// <param name="screenDipHeight">The screen's height in DIP.</param>
    /// <param name="toolbar">The live toolbar settings (dock and edge).</param>
    /// <param name="scaleX">Physical pixels per DIP, horizontally.</param>
    /// <param name="scaleY">Physical pixels per DIP, vertically.</param>
    /// <returns>The hit circle's centre (physical px).</returns>
    internal static PhysicalPoint SkipCenter(
        double screenDipWidth, double screenDipHeight, ToolbarSettings toolbar,
        double scaleX, double scaleY)
    {
        var atRight = !DockedAtRight(toolbar);
        var xDip = atRight ? screenDipWidth - SkipCenterInsetDip : SkipCenterInsetDip;
        return new PhysicalPoint(xDip * scaleX, screenDipHeight / 2 * scaleY);
    }
}
