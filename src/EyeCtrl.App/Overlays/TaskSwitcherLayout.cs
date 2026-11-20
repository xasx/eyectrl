namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// Pure DIP geometry for the fullscreen task switcher: the area the tile grid may occupy, how
/// many full columns and rows fit there, and where the centred block and the bottom hint row go.
///
/// <para>
/// The area starts clear of the ✕ in the top-right corner, the docked toolbar band (its REAL
/// band plus a clearance gap), the bottom hint row and the screen edges. Only full rows are laid
/// out — a row that does not fit is not drawn, exactly like the viewport clamp in
/// <see cref="TaskSwitcherWindow.VisibleTileRect"/> — and the block of shown rows is centred in
/// the remaining space, horizontally and vertically. No WPF type crosses this seam, so the
/// arithmetic unit-tests headlessly at any DPI.
/// </para>
/// </summary>
internal static class TaskSwitcherLayout
{
    /// <summary>
    /// The WrapPanel cell. S4-C grew the cell's height from 120 to 166; F-GAP doubled the gap
    /// between neighbouring tiles: the template's face margin grew from 8 to
    /// <see cref="TileFaceMarginDip"/> (16 DIP) and the cell with it, so the drawn face keeps its
    /// 184 × 150 — the preview box (168 × 94), the 20 DIP icon-and-title row and the progress bar
    /// are unchanged inside it. The XAML's WrapPanel ItemWidth/ItemHeight and the TileFace margin
    /// are pinned to these constants by the coupling pin in TaskSwitcherWindowLayoutTests: cell =
    /// face + 2 × margin, in both directions. The width growth drops the owner's grid from
    /// 9 × 5 = 45 to 8 × 5 = 40 tiles (the ticket's "about 40").
    /// </summary>
    internal const double CellWidth = 216;
    internal const double CellHeight = 182;

    /// <summary>
    /// The tile template's TileFace margin in DIP (TaskSwitcherWindow.xaml's
    /// <c>&lt;Border x:Name="TileFace" Margin="16"&gt;</c>). The margin is the inter-tile gap —
    /// two neighbouring faces sit 2 × this (32 DIP) apart — and lies outside the face, so it
    /// stays dead for gaze. Kept beside the cells it sizes, as the ONE source for the number:
    /// the XAML restates it and the coupling pin holds the two together.
    /// </summary>
    internal const double TileFaceMarginDip = 16;

    /// <summary>Minimum distance of the tile block from the left, top and right screen edges.</summary>
    internal const double EdgeMargin = 40;

    /// <summary>Clearance kept between the docked toolbar band and the tile block / hint row.</summary>
    internal const double ToolbarGap = 24;

    /// <summary>
    /// How far the close disc's painted halo reaches past its rim, in DIP, for the keep-out
    /// arithmetic. Conservative by 1.5 DIP: <c>DwellArcButton</c>'s 3 DIP StrokeHalo is inset by
    /// WPF's stroke rendering, so the real painted edge is +2.5 DIP (the pre-F-CORNER wording said
    /// "4 DIP" as if the stroke reached its full width outside the rim). The keep-out may stay
    /// conservative — an extra 1.5 DIP of dead band costs nothing, a clipped halo does.
    /// </summary>
    internal const double CloseHaloDip = 4;

    /// <summary>
    /// The bottom edge of the ✕'s painted box, DIP from the screen's top edge:
    /// <see cref="UIConstants.BackdropHideButtonOffset"/> (53, the shared corner offset the owner
    /// chose) + the r50 disc (<see cref="UIConstants.ButtonRadius"/>) + the conservative halo
    /// (<see cref="CloseHaloDip"/>) = 107. Tiles must start below this: the ✕ is the escape, and a
    /// tile under it would be a misfire (F-CORNER).
    /// </summary>
    internal const double CloseKeepOutBottom =
        UIConstants.BackdropHideButtonOffset + UIConstants.ButtonRadius + CloseHaloDip;

    /// <summary>
    /// The tile area's minimum top: <see cref="CloseKeepOutBottom"/> plus
    /// <see cref="ToolbarGap"/> = 131 DIP. It holds for every dock and at every rung of the
    /// degradation ladder — the ✕ is never dropped, because it is the only non-switching exit.
    /// </summary>
    internal const double TilesTopMinimum = CloseKeepOutBottom + ToolbarGap;

    /// <summary>
    /// The bottom hint row's height, reserved WHOLE: 56 DIP is the ceiling of the hint text's
    /// measured height (one 31.92 DIP line at the 24 DIP title size — F-HINT's "hint too small",
    /// for both "Segoe UI Variable Text" and its "Segoe UI" fallback — plus its 12 DIP top and
    /// bottom margins = 55.92). Reserving the ceiling keeps the centred tile block on a whole
    /// DIP — the owner's block lands at y 199.0, i.e. 398 device pixels at 200 % — instead of
    /// the 199.46 the exact text height produced. F-CORNER moved the ✕ out of this row to the
    /// top-right corner, so the row is text(-and-chip) only and shrank from 124 DIP; F-HINT
    /// then grew it from 43 to 56 for the owner's larger hint. <c>TaskSwitcherWindowLayoutTests</c>
    /// holds the reserve against what WPF actually lays out (reserved ≥ laid-out, and less than
    /// one DIP above it), which keeps the area arithmetic the layout WPF performs.
    /// </summary>
    internal const double HintRowHeight = 56;

    /// <summary>A rectangle in DIP.</summary>
    internal readonly record struct DipRect(double X, double Y, double Width, double Height)
    {
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
    }

    /// <summary>The computed layout.</summary>
    internal sealed record Result(
        DipRect Area,
        DipRect Block,
        int Columns,
        int Rows,
        int ShownRows,
        double HintBottomMargin)
    {
        /// <summary>How many tiles fit in full rows; tiles beyond this stay unreachable.</summary>
        internal int Capacity => Columns * Rows;
    }

    /// <summary>
    /// Computes the layout. <paramref name="toolbarBandDip"/> is the docked toolbar's real band in
    /// DIP (<c>null</c> when the band is floating or off screen) and <paramref name="dockedEdge"/>
    /// the edge it hugs ("Top"/"Bottom"/"Left"/"Right"). Only full rows whose cells fit inside the
    /// area are shown; the block the shown tiles occupy is centred in that area, in both
    /// directions.
    ///
    /// <para>
    /// <b>Degradation.</b> A gaze-only user must never be left with a fullscreen switcher whose only
    /// live target is the ✕, so when the keep-out leaves no full row or no full column the layout
    /// relaxes in a fixed order and stops as soon as one row x one column fits: (1) drop the
    /// clearance gap, (2) drop the 40 DIP edge margins, (3) drop the toolbar keep-out entirely. The
    /// ✕'s own keep-out (<see cref="TilesTopMinimum"/>) is NEVER dropped: the ✕ is the escape, and
    /// a tile under it would make the escape a misfire. The last step is the only one that can put
    /// a tile under the toolbar band; T1's landing guard still protects the opening look there. On
    /// the owner's screen a 200 DIP band leaves 5 full rows of the 182 DIP cell (the text-only hint
    /// row bought the fifth back), so the ladder only runs on degenerate displays.
    /// </para>
    /// </summary>
    internal static Result Compute(
        double screenWidthDip, double screenHeightDip,
        DipRect? toolbarBandDip, string? dockedEdge, int tileCount)
    {
        Result? last = null;
        for (var level = 0; level < 4; level++)
        {
            var gap = level >= 1 ? 0.0 : ToolbarGap;
            var margin = level >= 2 ? 0.0 : EdgeMargin;
            var band = level >= 3 ? null : toolbarBandDip;
            var edge = level >= 3 ? null : dockedEdge;

            last = ComputeAt(screenWidthDip, screenHeightDip, band, edge, gap, margin, tileCount);
            if (last.Columns >= 1 && last.Rows >= 1)
                return last;
        }

        return last!;
    }

    private static Result ComputeAt(
        double screenWidthDip, double screenHeightDip,
        DipRect? toolbarBandDip, string? dockedEdge,
        double gap, double margin, int tileCount)
    {
        var edge = dockedEdge?.ToLowerInvariant();

        // A bottom-docked band pushes the hint row up by the band's own thickness plus the gap.
        // This is what keeps the ✕ and the hint text off the band, not just the tiles.
        var hintBottomMargin = toolbarBandDip is { } band && edge == "bottom"
            ? Math.Max(0, screenHeightDip - band.Y) + gap
            : 0.0;

        var left = margin;
        var top = margin;
        var right = screenWidthDip - margin;
        var bottom = screenHeightDip - HintRowHeight - hintBottomMargin;

        if (toolbarBandDip is { } rect)
        {
            switch (edge)
            {
                case "top":
                    top = Math.Max(top, rect.Bottom + gap);
                    break;
                case "bottom":
                    bottom = Math.Min(bottom, rect.Y - gap);
                    break;
                case "left":
                    left = Math.Max(left, rect.Right + gap);
                    break;
                case "right":
                    right = Math.Min(right, rect.X - gap);
                    break;
            }
        }

        // F-CORNER: the ✕ sits at (screen width − 53, 53), and its disc + halo reach 107 DIP down
        // from the top edge. Every rung of the ladder keeps this floor, whatever the dock and
        // whether or not the toolbar band / edge margins are still applied: a tile under the ✕
        // would be switched by a gaze aimed at the escape (the owner controls the PC by eye).
        top = Math.Max(top, TilesTopMinimum);

        // Degenerate displays: never hand WPF a negative area (it would arrange outside the window
        // and register targets on nothing). A zero area simply has zero capacity.
        left = Math.Clamp(left, 0, screenWidthDip);
        right = Math.Clamp(right, left, screenWidthDip);
        top = Math.Clamp(top, 0, screenHeightDip);
        bottom = Math.Clamp(bottom, top, screenHeightDip);

        var area = new DipRect(left, top, right - left, bottom - top);
        var columns = (int)Math.Floor(area.Width / CellWidth);
        var rows = (int)Math.Floor(area.Height / CellHeight);

        // The block wraps the cells the shown tiles actually occupy, in BOTH directions. A session
        // with fewer windows than columns must not size the viewport to the full grid width and let
        // the WrapPanel fill it from the left: that put a five-tile row 400 DIP left of centre at
        // 1920 (review finding 1). The same holds for the rows: a single partial row sits in the
        // vertical middle of the area, not at its top.
        var usedColumns = Math.Min(columns, Math.Max(0, tileCount));
        var shownRows = usedColumns == 0
            ? 0
            : Math.Min(rows, (int)Math.Ceiling(tileCount / (double)usedColumns));

        var blockWidth = usedColumns * CellWidth;
        var blockHeight = shownRows * CellHeight;
        var block = new DipRect(
            area.X + ((area.Width - blockWidth) / 2),
            area.Y + ((area.Height - blockHeight) / 2),
            blockWidth,
            blockHeight);

        return new Result(area, block, columns, rows, shownRows, hintBottomMargin);
    }

    /// <summary>
    /// Converts physical pixels to DIP exactly once. Production passes the live
    /// <see cref="EyeCtrl.Core.Services.DpiHelper"/> scale; a caller that divides again (or scales
    /// the result a second time) moves the keep-out, which the 200 % geometry test pins.
    /// </summary>
    internal static DipRect ToDip(PhysicalRect physical, double scaleX, double scaleY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleY);
        return new DipRect(
            physical.X / scaleX, physical.Y / scaleY,
            physical.Width / scaleX, physical.Height / scaleY);
    }
}
