namespace EyeCtrl.App.Tests;

using EyeCtrl.App.Overlays;
using EyeCtrl.Platform.Thumbnails;
using EyeCtrl.ViewModels;

/// <summary>
/// The task switcher's tile-area geometry, unit-tested headlessly (no window, no display): the
/// tiles must start clear of the ✕ in the top-right corner, the docked toolbar band and the
/// screen edges, only full rows are shown, and the block is centred in the space between the
/// keeps-outs and the bottom hint row.
/// </summary>
public class TaskSwitcherLayoutTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    private static TaskSwitcherLayout.DipRect Band(double x, double y, double width, double height) =>
        new(x, y, width, height);

    /// <summary>
    /// The ✕'s painted box in DIP: the disc's centre at (screen width − 53, 53), the r50 disc and
    /// the conservative 4 DIP halo — the same arithmetic the keep-out uses, never a restated
    /// rectangle. Tiles must stay outside it.
    /// </summary>
    private static TaskSwitcherLayout.DipRect CloseDiscBox(double screenWidth) =>
        new(
            screenWidth - UIConstants.BackdropHideButtonOffset - UIConstants.ButtonRadius
                - TaskSwitcherLayout.CloseHaloDip,
            UIConstants.BackdropHideButtonOffset - UIConstants.ButtonRadius
                - TaskSwitcherLayout.CloseHaloDip,
            (2 * (UIConstants.ButtonRadius + TaskSwitcherLayout.CloseHaloDip)),
            (2 * (UIConstants.ButtonRadius + TaskSwitcherLayout.CloseHaloDip)));

    /// <summary>
    /// The cell grid the block holds, in display order: cell = <see cref="TaskSwitcherLayout.CellWidth"/>
    /// × <see cref="TaskSwitcherLayout.CellHeight"/>, face inset one template margin inside it.
    /// </summary>
    private static IEnumerable<TaskSwitcherLayout.DipRect> Cells(
        TaskSwitcherLayout.Result layout, int tileCount)
    {
        var count = Math.Min(tileCount, layout.Capacity);
        for (var i = 0; i < count; i++)
        {
            var column = i % layout.Columns;
            var row = i / layout.Columns;
            yield return new TaskSwitcherLayout.DipRect(
                layout.Block.X + (column * TaskSwitcherLayout.CellWidth),
                layout.Block.Y + (row * TaskSwitcherLayout.CellHeight),
                TaskSwitcherLayout.CellWidth,
                TaskSwitcherLayout.CellHeight);
        }
    }

    private static void AssertFacesClearOfScreenEdges(TaskSwitcherLayout.Result layout)
    {
        // The block is made of 216x182 cells; the drawn face is inset another 16 DIP inside each
        // cell, so a block edge at the margin keeps the FACE at margin + 16. Checking the block is
        // the conservative form: face >= block edge.
        Check.That(layout.Block.X).IsGreaterOrEqualThan(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Y).IsGreaterOrEqualThan(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Right).IsLessOrEqualThan(ScreenWidth - TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Bottom).IsLessOrEqualThan(ScreenHeight - TaskSwitcherLayout.EdgeMargin);
    }

    [Fact]
    public void DockedTopAtTheOwnersRealBand140_OnTheOwners1920x1200Screen_HoldsFiveRowsOfEight_40Tiles()
    {
        // The owner's REAL band: toolbar.size 108 grows the toolbar window to 108 + 2 x 12
        // (ToolbarBand.ButtonMargin) + 2 x 4 (ToolbarWindow.BandPaddingDip) = 140 DIP, docked Top
        // at the work area's top. F-GAP: the 216x182 cell keeps the face at 184x150 and doubles
        // the gap between neighbouring faces to 2 x 16 = 32 DIP, so the grid drops from
        // 9 x 5 = 45 to 8 x 5 = 40 tiles (the tile-count promise of the ticket). F-CORNER: the
        // hint row is text-only (reserved 43 DIP) and the ✕ keep-out (131) is below the 164 the band
        // already forces, so the area grew 81 DIP taller and the block moved down 40.5.
        // F-HINT now grows the row to 56 DIP for the owner's 24 DIP hint, so every block y below
        // moves up by half that 13 DIP growth; the capacity stays 8 x 5 = 40.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 140), "Top", tileCount: 63);

        Check.That(layout.Area.X).IsEqualTo(40);
        Check.That(layout.Area.Y).IsEqualTo(164);
        Check.That(layout.Area.Width).IsEqualTo(1840);
        Check.That(layout.Area.Height)
            .IsCloseTo(ScreenHeight - TaskSwitcherLayout.HintRowHeight - 164, 1e-9);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Capacity).IsEqualTo(40);
        Check.That(layout.ShownRows).IsEqualTo(5);
        Check.That(layout.Block.X).IsEqualTo(96);
        Check.That(layout.Block.Y).IsCloseTo(199.0, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1728);
        Check.That(layout.Block.Height).IsEqualTo(910);
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        AssertFacesClearOfScreenEdges(layout);

        // The two numbers the ticket promises, derived from the constants (never restated):
        // the face keeps 184 x 150, and neighbouring faces sit 32 DIP apart.
        Check.That(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip)
            .IsEqualTo(184);
        Check.That(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip)
            .IsEqualTo(150);
        Check.That(2 * TaskSwitcherLayout.TileFaceMarginDip).IsEqualTo(32);
    }

    [Theory]
    [InlineData(40.0, 5)]
    [InlineData(112.0, 5)]
    [InlineData(200.0, 5)]
    public void DockedTop_AtEverySize_LeavesTheBandAndItsGapClear(double toolbarSize, int expectedRows)
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, toolbarSize), "Top", tileCount: 200);

        // A shallow band no longer governs the top: the ✕ keep-out (131) is deeper than a 40 DIP
        // band plus its clearance (64). The band governs from 107 DIP upward.
        Check.That(layout.Area.Y)
            .IsEqualTo(Math.Max(toolbarSize + TaskSwitcherLayout.ToolbarGap, TaskSwitcherLayout.TilesTopMinimum));
        Check.That(layout.Block.Y).IsGreaterOrEqualThan(layout.Area.Y);
        Check.That(layout.Rows).IsEqualTo(expectedRows);
        Check.That(layout.Columns).IsEqualTo(8);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Theory]
    [InlineData(40.0, 5)]
    [InlineData(112.0, 4)]
    [InlineData(200.0, 4)]
    public void DockedBottom_KeepsTheHintRowFullyAboveTheBand(double toolbarSize, int expectedRows)
    {
        var bandTop = ScreenHeight - toolbarSize;
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, bandTop, ScreenWidth, toolbarSize), "Bottom",
            tileCount: 200);

        Check.That(layout.HintBottomMargin).IsEqualTo(toolbarSize + TaskSwitcherLayout.ToolbarGap);

        // The hint row is the constant-height strip sitting HintBottomMargin above the screen's
        // bottom edge. Its bottom must stay the clearance gap above the band's top.
        var hintRowBottom = ScreenHeight - layout.HintBottomMargin;
        Check.That(hintRowBottom).IsLessOrEqualThan(
            bandTop - TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Block.Bottom).IsLessOrEqualThan(
            hintRowBottom - TaskSwitcherLayout.HintRowHeight);
        Check.That(layout.Rows).IsEqualTo(expectedRows);
        Check.That(layout.Area.Y).IsEqualTo(TaskSwitcherLayout.TilesTopMinimum);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Theory]
    [InlineData(40.0, 8)]
    [InlineData(112.0, 8)]
    [InlineData(200.0, 7)]
    public void DockedLeft_LeavesTheBandAndItsGapClear(double toolbarSize, int expectedColumns)
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, toolbarSize, ScreenHeight), "Left", tileCount: 200);

        Check.That(layout.Area.X).IsEqualTo(toolbarSize + TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Block.X).IsGreaterOrEqualThan(
            toolbarSize + TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Columns).IsEqualTo(expectedColumns);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Theory]
    [InlineData(40.0, 8)]
    [InlineData(112.0, 8)]
    [InlineData(200.0, 7)]
    public void DockedRight_LeavesTheBandAndItsGapClear(double toolbarSize, int expectedColumns)
    {
        var bandLeft = ScreenWidth - toolbarSize;
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(bandLeft, 0, toolbarSize, ScreenHeight), "Right",
            tileCount: 200);

        Check.That(layout.Area.Right).IsEqualTo(bandLeft - TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Block.Right).IsLessOrEqualThan(
            bandLeft - TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Columns).IsEqualTo(expectedColumns);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Fact]
    public void FloatingOrHiddenToolbar_UsesOnlyTheScreenEdgeMarginsAndTheCloseKeepOut()
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, toolbarBandDip: null, dockedEdge: null, tileCount: 63);

        Check.That(layout.Area.X).IsEqualTo(40);
        Check.That(layout.Area.Y).IsEqualTo(TaskSwitcherLayout.TilesTopMinimum);
        Check.That(layout.Area.Width).IsEqualTo(1840);
        Check.That(layout.Area.Height)
            .IsCloseTo(ScreenHeight - TaskSwitcherLayout.HintRowHeight - TaskSwitcherLayout.TilesTopMinimum, 1e-9);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Capacity).IsEqualTo(40);
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block.X).IsEqualTo(96);
        Check.That(layout.Block.Y).IsCloseTo(182.5, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1728);
        Check.That(layout.Block.Height).IsEqualTo(910);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Fact]
    public void TilesBeyondCapacity_AreNotDrawn_AndKeepTheFullBlock()
    {
        // 64 windows: one more than the 8x5 capacity. Only the five full rows are shown — the
        // block stays the full capacity width and height, and the sixth row lies wholly below
        // the viewport (the window's VisibleTileRect clamp gives those faces no target).
        var overflow = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount: 64);
        Check.That(overflow.ShownRows).IsEqualTo(5);
        Check.That(overflow.Capacity).IsEqualTo(40);
        Check.That(overflow.Block.X).IsEqualTo(96);
        Check.That(overflow.Block.Y).IsCloseTo(185.0, 1e-9);
        Check.That(overflow.Block.Width).IsEqualTo(1728);
        Check.That(overflow.Block.Height).IsEqualTo(910);
    }

    [Theory]
    [InlineData(1, 216, 182, 852, 549.0)]
    [InlineData(5, 1080, 182, 420, 549.0)]
    // Tiles == columns (8) fills exactly one row — the Math.Ceiling boundary; 10 wraps into a second.
    [InlineData(8, 1728, 182, 96, 549.0)]
    [InlineData(10, 1728, 364, 96, 458.0)]
    [InlineData(64, 1728, 910, 96, 185.0)]
    public void TheBlockWrapsOnlyTheCellsTheTilesOccupy_AndStaysCentred(
        int tileCount, double width, double height, double x, double y)
    {
        // Review finding 1: sizing the block to full columns left a short row at the left screen
        // edge (five tiles spanned 68..1052, centre 560). The block must wrap the shown cells and
        // stay centred on the area (1920 wide: centre 960; the 112 DIP-band area is
        // 136..(1200 - 56), centre 640.0).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount);

        Check.That(layout.Block.X).IsEqualTo(x);
        Check.That(layout.Block.Y).IsCloseTo(y, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(width);
        Check.That(layout.Block.Height).IsEqualTo(height);
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(960, 1e-9);
        Check.That(layout.Block.Y + (layout.Block.Height / 2)).IsCloseTo(640.0, 1e-9);
    }

    [Fact]
    public void FewTiles_AtALeftDock_AreCentredInTheAreaBesideTheBand()
    {
        // Left 112: area (136,131,1744,1013). A short row must centre on the available area's
        // centre (1008), not on the screen's (960).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, 112, ScreenHeight), "Left", tileCount: 5);

        Check.That(layout.Block.X).IsEqualTo(468);
        Check.That(layout.Block.Y).IsCloseTo(546.5, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1080);
        Check.That(layout.Block.Height).IsEqualTo(182);
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(1008, 1e-9);
        Check.That(layout.Block.X).IsGreaterOrEqualThan(112 + TaskSwitcherLayout.ToolbarGap);
    }

    [Fact]
    public void FewTiles_AtARightDock_AreCentredInTheAreaBesideTheBand()
    {
        // Right 112: area (40,131,1744,1013), centre 912.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(ScreenWidth - 112, 0, 112, ScreenHeight), "Right",
            tileCount: 5);

        Check.That(layout.Block.X).IsEqualTo(372);
        Check.That(layout.Block.Y).IsCloseTo(546.5, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1080);
        Check.That(layout.Block.Height).IsEqualTo(182);
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(912, 1e-9);
        Check.That(layout.Block.Right).IsLessOrEqualThan(
            ScreenWidth - 112 - TaskSwitcherLayout.ToolbarGap);
    }

    [Fact]
    public void FewTiles_At200Percent_UseTheSameDipBlock()
    {
        // Same 112 DIP band reached through the 200 % physical rect: a short row must wrap and
        // centre identically (420,549.0,1080,182).
        var physicalBand = new EyeCtrl.Core.Models.PhysicalRect(0, 0, 3840, 224);
        var at200 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, TaskSwitcherLayout.ToDip(physicalBand, 2.0, 2.0), "Top", 5);
        var at100 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, 1920, 112), "Top", 5);

        Check.That(at200.Block.X).IsEqualTo(420);
        Check.That(at200.Block.Y).IsCloseTo(549.0, 1e-9);
        Check.That(at200.Block.Width).IsEqualTo(1080);
        Check.That(at200.Block.Height).IsEqualTo(182);
        Check.That(at200.Block).IsEqualTo(at100.Block);
    }

    [Fact]
    public void PhysicalToolbarBounds_ConvertToDipExactlyOnce_At200Percent()
    {
        // The owner runs at 200%: a 112 DIP band is 224 PHYSICAL pixels tall. Dividing once gives
        // the same DIP band the 100% case uses; the geometry must be identical, not half or double.
        var physicalBand = new EyeCtrl.Core.Models.PhysicalRect(0, 0, 3840, 224);
        var converted = TaskSwitcherLayout.ToDip(physicalBand, 2.0, 2.0);
        Check.That(converted).IsEqualTo(Band(0, 0, 1920, 112));

        var at200 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, converted, "Top", tileCount: 63);
        var at100 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, 1920, 112), "Top", tileCount: 63);

        Check.That(at200.Area).IsEqualTo(at100.Area);
        Check.That(at200.Block).IsEqualTo(at100.Block);
        Check.That(at200.Columns).IsEqualTo(8);
        Check.That(at200.Rows).IsEqualTo(5);
    }

    [Fact]
    public void WhenTheKeepOutLeavesNoRow_TheClearanceGapIsDroppedFirst()
    {
        // A 960 DIP top band leaves 160 DIP before the hint row with the 24 DIP clearance: not
        // one full row. Dropping the gap leaves 184 DIP, which holds one 182 DIP row. The edge margins stay,
        // and so does the ✕'s own keep-out (960 > 131, so the band governs here).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 960), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(1);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Area.Y).IsEqualTo(960);
        Check.That(layout.Area.X).IsEqualTo(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.X).IsEqualTo(96);
        Check.That(layout.Block.Y).IsCloseTo(961.0, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1728);
        Check.That(layout.Block.Height).IsEqualTo(182);
    }

    [Fact]
    public void WhenDroppingTheGapStillLeavesNoRow_TheEdgeMarginsAreDroppedNext()
    {
        // 230 DIP of screen: 150 DIP between the edge margins cannot hold a 216 DIP column, but the
        // full 230 DIP can hold one once the margins are dropped. The ✕ keep-out still starts the
        // area at 131, so the single cell sits in the middle of what is left.
        var layout = TaskSwitcherLayout.Compute(
            230, ScreenHeight, toolbarBandDip: null, dockedEdge: null, tileCount: 1);

        Check.That(layout.Columns).IsEqualTo(1);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Area.X).IsEqualTo(0);
        Check.That(layout.Area.Y).IsEqualTo(TaskSwitcherLayout.TilesTopMinimum);
        Check.That(layout.Area.Width).IsEqualTo(230);
        Check.That(layout.Area.Height)
            .IsCloseTo(ScreenHeight - TaskSwitcherLayout.HintRowHeight - TaskSwitcherLayout.TilesTopMinimum, 1e-9);
        Check.That(layout.Block.X).IsEqualTo(7);
        Check.That(layout.Block.Y).IsCloseTo(546.5, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(216);
        Check.That(layout.Block.Height).IsEqualTo(182);
    }

    [Fact]
    public void WhenEvenTheMarginsAreDropped_NoRowFits_TheToolbarKeepOutIsDroppedLast()
    {
        // A 1000 DIP top band leaves 144 DIP above the hint row even with margins and gap
        // gone; the last resort is to ignore the band (the T1 landing guard covers the landing).
        // The ✕'s own keep-out is NOT part of that trade: it survives at 131, so no tile can sit
        // under the escape.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 1000), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Area.X).IsEqualTo(0);
        Check.That(layout.Area.Y).IsEqualTo(TaskSwitcherLayout.TilesTopMinimum);
        Check.That(layout.Area.Width).IsEqualTo(ScreenWidth);
        Check.That(layout.Area.Height)
            .IsCloseTo(ScreenHeight - TaskSwitcherLayout.HintRowHeight - TaskSwitcherLayout.TilesTopMinimum, 1e-9);
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block.X).IsEqualTo(96);
        Check.That(layout.Block.Y).IsCloseTo(182.5, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1728);
        Check.That(layout.Block.Height).IsEqualTo(910);
    }

    [Fact]
    public void TheCloseKeepOut_IsTheSharedCornerOffsetPlusTheDiscAndAHaloPlusTheGap()
    {
        // 53 (UIConstants.BackdropHideButtonOffset) + 50 (r50 disc) + 4 (conservative halo) = 107;
        // plus the 24 DIP clearance every keep-out keeps from the tiles = 131. Derived, not
        // restated: a mutant at 40 instead of 53 must move all four numbers.
        Check.That(TaskSwitcherLayout.CloseKeepOutBottom).IsEqualTo(
            UIConstants.BackdropHideButtonOffset + UIConstants.ButtonRadius + TaskSwitcherLayout.CloseHaloDip);
        Check.That(TaskSwitcherLayout.CloseKeepOutBottom).IsEqualTo(107);
        Check.That(TaskSwitcherLayout.TilesTopMinimum).IsEqualTo(TaskSwitcherLayout.CloseKeepOutBottom + TaskSwitcherLayout.ToolbarGap);
        Check.That(TaskSwitcherLayout.TilesTopMinimum).IsEqualTo(131);
    }

    [Theory]
    [InlineData("Top")]
    [InlineData("Bottom")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("none")]
    public void TheTiles_NeverReachTheCloseDisc_AtEveryDock_TheOwnerScreenAndASmallOne(string dock)
    {
        foreach (var (screenWidth, screenHeight) in new[] { (ScreenWidth, ScreenHeight), (1024.0, 768.0) })
        {
            var (band, edge) = BandForDock(dock, screenWidth, screenHeight);

            var layout = TaskSwitcherLayout.Compute(screenWidth, screenHeight, band, edge, tileCount: 200);
            var close = CloseDiscBox(screenWidth);

            Check.WithCustomMessage($"{dock} at {screenWidth}x{screenHeight}: the area starts at {layout.Area.Y}.")
                .That(layout.Area.Y).IsGreaterOrEqualThan(TaskSwitcherLayout.TilesTopMinimum);

            // Every cell the grid would draw (cells, not faces: the 16 DIP margin is dead space,
            // so a cell is the conservative bound) clears the ✕'s painted box.
            foreach (var cell in Cells(layout, 200))
            {
                Check.WithCustomMessage($"{dock} at {screenWidth}x{screenHeight}: a cell {cell} reaches the ✕ {close}.")
                    .That(cell.Y).IsGreaterOrEqualThan(TaskSwitcherLayout.TilesTopMinimum);
                Check.That(Intersects(cell, close)).IsFalse();
            }
        }
    }

    [Fact]
    public void NoThumbnailRect_CanIntersectTheCloseDisc()
    {
        // A DWM thumbnail is composed ABOVE every WPF pixel, so its destination rect — not the
        // tile's clipping — is what must stay off the ✕. The preview box (168 x 94) sits inside
        // the face's 8 DIP padding, and FitDestination always returns a rect INSIDE its box, so a
        // thumbnail's top edge is at least face.Top + 8. The keep-out makes face.Top >= 131 + 16,
        // i.e. >= 147, which is 40 DIP below the ✕'s painted bottom (107).
        foreach (var dock in new[] { "Top", "Bottom", "Left", "Right", "none" })
        {
            var (band, edge) = BandForDock(dock, ScreenWidth, ScreenHeight);

            var layout = TaskSwitcherLayout.Compute(ScreenWidth, ScreenHeight, band, edge, tileCount: 200);
            var close = CloseDiscBox(ScreenWidth);

            foreach (var cell in Cells(layout, 200))
            {
                var face = new TaskSwitcherLayout.DipRect(
                    cell.X + TaskSwitcherLayout.TileFaceMarginDip,
                    cell.Y + TaskSwitcherLayout.TileFaceMarginDip,
                    TaskSwitcherLayout.CellWidth - (2 * TaskSwitcherLayout.TileFaceMarginDip),
                    TaskSwitcherLayout.CellHeight - (2 * TaskSwitcherLayout.TileFaceMarginDip));

                // The preview box's most extreme possible placement inside the face (top at the
                // face's 8 DIP padding). FitDestination fits INSIDE the box it is given.
                var boxWidth = 168.0;
                var boxHeight = 94.0;
                var boxLeft = face.X + ((face.Width - boxWidth) / 2);
                var boxTop = face.Y + 8;
                var thumbnail = ThumbnailGeometry.FitDestination(
                    boxLeft, boxTop, boxWidth, boxHeight, sourceWidth: 1280, sourceHeight: 720);

                Check.WithCustomMessage($"{dock}: thumbnail {thumbnail} at face {face} is not inside its box.")
                    .That(thumbnail.Y).IsGreaterOrEqualThan(boxTop - 0.5);
                Check.That(thumbnail.X).IsGreaterOrEqualThan(boxLeft - 0.5);
                Check.That(Intersects(thumbnail, close)).IsFalse();
            }
        }
    }

    private static bool Intersects(TaskSwitcherLayout.DipRect a, TaskSwitcherLayout.DipRect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static bool Intersects(EyeCtrl.Core.Models.PhysicalRect a, TaskSwitcherLayout.DipRect b) =>
        a.X < b.Right && a.X + a.Width > b.X && a.Y < b.Bottom && a.Y + a.Height > b.Y;

    /// <summary>The toolbar band for one dock name, or <c>null</c> for a floating/hidden band.</summary>
    private static (TaskSwitcherLayout.DipRect? Band, string? Edge) BandForDock(
        string dock, double screenWidth, double screenHeight) => dock switch
    {
        "Top" => (Band(0, 0, screenWidth, 140), "Top"),
        "Bottom" => (Band(0, screenHeight - 140, screenWidth, 140), "Bottom"),
        "Left" => (Band(0, 0, 140, screenHeight), "Left"),
        "Right" => (Band(screenWidth - 140, 0, 140, screenHeight), "Right"),
        _ => (null, null),
    };
}
