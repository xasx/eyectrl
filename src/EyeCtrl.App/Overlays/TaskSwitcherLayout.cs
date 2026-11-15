namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core.Models;

/// <summary>
/// Pure DIP geometry for the fullscreen task switcher: the area the tile grid may occupy, how
/// many full columns and rows fit there, and where the centred block and the bottom hint row go.
///
/// <para>
/// The area starts clear of the docked toolbar band (its REAL band plus a clearance gap), the
/// bottom hint row and the screen edges. Only full rows are laid out — a row that does not fit is
/// not drawn, exactly like the viewport clamp in <see cref="TaskSwitcherWindow.VisibleTileRect"/>
/// — and the block of shown rows is centred in the remaining space, horizontally and vertically.
/// No WPF type crosses this seam, so the arithmetic unit-tests headlessly at any DPI.
/// </para>
/// </summary>
internal static class TaskSwitcherLayout
{
    /// <summary>The WrapPanel cell (the drawn face is inset by the template's 8 DIP margin).</summary>
    internal const double CellWidth = 200;
    internal const double CellHeight = 120;

    /// <summary>Minimum distance of the tile block from the left, top and right screen edges.</summary>
    internal const double EdgeMargin = 40;

    /// <summary>Clearance kept between the docked toolbar band and the tile block / hint row.</summary>
    internal const double ToolbarGap = 24;

    /// <summary>
    /// The bottom hint/close row's height: the 100 DIP close disc (2 x the 50 DIP hit radius) plus
    /// its 12 DIP top and bottom margins. The window pins its row to this height, so the area
    /// arithmetic below is the layout WPF actually performs.
    /// </summary>
    internal const double HintRowHeight = 124;

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
    /// last step is the only one that can put a tile under the band; T1's landing guard still
    /// protects the opening look there. On the owner's screen a 200 DIP band leaves 7 rows, so the
    /// ladder only runs on degenerate displays.
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
