namespace EyeCtrl.App.Tests;

using EyeCtrl.App.Overlays;

/// <summary>
/// The task switcher's tile-area geometry, unit-tested headlessly (no window, no display): the
/// tiles must start clear of the docked toolbar band and the screen edges, only full rows are
/// shown, and the block is centred in the space between the band and the bottom hint row.
/// </summary>
public class TaskSwitcherLayoutTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    private static TaskSwitcherLayout.DipRect Band(double x, double y, double width, double height) =>
        new(x, y, width, height);

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
        // 9 x 5 = 45 to 8 x 5 = 40 tiles (the tile-count promise of the ticket).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 140), "Top", tileCount: 63);

        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(40, 164, 1840, 912));
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Capacity).IsEqualTo(40);
        Check.That(layout.ShownRows).IsEqualTo(5);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(96, 165, 1728, 910));
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
    [InlineData(200.0, 4)]
    public void DockedTop_AtEverySize_LeavesTheBandAndItsGapClear(double toolbarSize, int expectedRows)
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, toolbarSize), "Top", tileCount: 200);

        Check.That(layout.Area.Y).IsEqualTo(toolbarSize + TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Block.Y).IsGreaterOrEqualThan(
            toolbarSize + TaskSwitcherLayout.ToolbarGap);
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
    public void FloatingOrHiddenToolbar_UsesOnlyTheScreenEdgeMargins()
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, toolbarBandDip: null, dockedEdge: null, tileCount: 63);

        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(40, 40, 1840, 1036));
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Capacity).IsEqualTo(40);
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(96, 103, 1728, 910));
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
        Check.That(overflow.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(96, 151, 1728, 910));
    }

    [Theory]
    [InlineData(1, 216, 182, 852, 515)]
    [InlineData(5, 1080, 182, 420, 515)]
    // Tiles == columns (8) fills exactly one row — the Math.Ceiling boundary; 10 wraps into a second.
    [InlineData(8, 1728, 182, 96, 515)]
    [InlineData(10, 1728, 364, 96, 424)]
    [InlineData(64, 1728, 910, 96, 151)]
    public void TheBlockWrapsOnlyTheCellsTheTilesOccupy_AndStaysCentred(
        int tileCount, double width, double height, double x, double y)
    {
        // Review finding 1: sizing the block to full columns left a short row at the left screen
        // edge (five tiles spanned 68..1052, centre 560). The block must wrap the shown cells and
        // stay centred on the area (1920 wide: centre 960; area 136..1076: centre 606).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount);

        Check.That(layout.Block).IsEqualTo(new TaskSwitcherLayout.DipRect(x, y, width, height));
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(960, 1e-9);
        Check.That(layout.Block.Y + (layout.Block.Height / 2)).IsCloseTo(606, 1e-9);
    }

    [Fact]
    public void FewTiles_AtALeftDock_AreCentredInTheAreaBesideTheBand()
    {
        // Left 112: area (136,40,1744,1036). A short row must centre on the available area's
        // centre (1008), not on the screen's (960).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, 112, ScreenHeight), "Left", tileCount: 5);

        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(468, 467, 1080, 182));
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(1008, 1e-9);
        Check.That(layout.Block.X).IsGreaterOrEqualThan(112 + TaskSwitcherLayout.ToolbarGap);
    }

    [Fact]
    public void FewTiles_AtARightDock_AreCentredInTheAreaBesideTheBand()
    {
        // Right 112: area (40,40,1744,1036), centre 912.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(ScreenWidth - 112, 0, 112, ScreenHeight), "Right",
            tileCount: 5);

        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(372, 467, 1080, 182));
        Check.That(layout.Block.X + (layout.Block.Width / 2)).IsCloseTo(912, 1e-9);
        Check.That(layout.Block.Right).IsLessOrEqualThan(
            ScreenWidth - 112 - TaskSwitcherLayout.ToolbarGap);
    }

    [Fact]
    public void FewTiles_At200Percent_UseTheSameDipBlock()
    {
        // Same 112 DIP band reached through the 200 % physical rect: a short row must wrap and
        // centre identically (420,515,1080,182).
        var physicalBand = new EyeCtrl.Core.Models.PhysicalRect(0, 0, 3840, 224);
        var at200 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, TaskSwitcherLayout.ToDip(physicalBand, 2.0, 2.0), "Top", 5);
        var at100 = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, 1920, 112), "Top", 5);

        Check.That(at200.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(420, 515, 1080, 182));
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
        // An 880 DIP top band leaves 196 DIP before the hint row: not one full row with the 24 DIP
        // clearance, but one 182 DIP row once the gap is dropped. The edge margins stay.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 880), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(1);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Area.Y).IsEqualTo(880);
        Check.That(layout.Area.X).IsEqualTo(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(96, 887, 1728, 182));
    }

    [Fact]
    public void WhenDroppingTheGapStillLeavesNoRow_TheEdgeMarginsAreDroppedNext()
    {
        // 230 DIP of screen: 150 DIP between the edge margins cannot hold a 216 DIP column, but the
        // full 230 DIP can hold one once the margins are dropped.
        var layout = TaskSwitcherLayout.Compute(
            230, ScreenHeight, toolbarBandDip: null, dockedEdge: null, tileCount: 1);

        Check.That(layout.Columns).IsEqualTo(1);
        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(0, 0, 230, 1076));
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(7, 447, 216, 182));
    }

    [Fact]
    public void WhenEvenTheMarginsAreDropped_NoRowFits_TheToolbarKeepOutIsDroppedLast()
    {
        // A 1000 DIP top band leaves 76 DIP above the hint row even with margins and gap gone; the
        // last resort is to ignore the band (the T1 landing guard covers the landing).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 1000), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(5);
        Check.That(layout.Columns).IsEqualTo(8);
        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(0, 0, ScreenWidth, 1076));
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(96, 83, 1728, 910));
    }
}
