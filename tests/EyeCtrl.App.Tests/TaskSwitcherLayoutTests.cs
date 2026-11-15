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
        // The block is made of 200x120 cells; the drawn face is inset another 8 DIP inside each
        // cell, so a block edge at the margin keeps the FACE at margin + 8. Checking the block is
        // the conservative form: face >= block edge.
        Check.That(layout.Block.X).IsGreaterOrEqualThan(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Y).IsGreaterOrEqualThan(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Right).IsLessOrEqualThan(ScreenWidth - TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block.Bottom).IsLessOrEqualThan(ScreenHeight - TaskSwitcherLayout.EdgeMargin);
    }

    [Fact]
    public void DockedTopAt112_OnTheOwners1920x1200Screen_HoldsSevenRowsOfNine_63Tiles()
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount: 63);

        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(40, 136, 1840, 940));
        Check.That(layout.Columns).IsEqualTo(9);
        Check.That(layout.Rows).IsEqualTo(7);
        Check.That(layout.Capacity).IsEqualTo(63);
        Check.That(layout.ShownRows).IsEqualTo(7);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(60, 186, 1800, 840));
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Theory]
    [InlineData(40.0, 8)]
    [InlineData(112.0, 7)]
    [InlineData(200.0, 7)]
    public void DockedTop_AtEverySize_LeavesTheBandAndItsGapClear(double toolbarSize, int expectedRows)
    {
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, toolbarSize), "Top", tileCount: 200);

        Check.That(layout.Area.Y).IsEqualTo(toolbarSize + TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Block.Y).IsGreaterOrEqualThan(
            toolbarSize + TaskSwitcherLayout.ToolbarGap);
        Check.That(layout.Rows).IsEqualTo(expectedRows);
        Check.That(layout.Columns).IsEqualTo(9);
        AssertFacesClearOfScreenEdges(layout);
    }

    [Theory]
    [InlineData(40.0, 8)]
    [InlineData(112.0, 7)]
    [InlineData(200.0, 6)]
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
    [InlineData(40.0, 9)]
    [InlineData(112.0, 8)]
    [InlineData(200.0, 8)]
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
    [InlineData(40.0, 9)]
    [InlineData(112.0, 8)]
    [InlineData(200.0, 8)]
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
        Check.That(layout.Columns).IsEqualTo(9);
        Check.That(layout.Rows).IsEqualTo(8);
        Check.That(layout.Capacity).IsEqualTo(72);
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(60, 138, 1800, 840));
        AssertFacesClearOfScreenEdges(layout);
    }

    [Fact]
    public void TilesBeyondCapacity_AreNotDrawn_AndFewTilesCentreOnlyTheRowsTheyNeed()
    {
        // 64 windows: one more than the 9x7 capacity. Only the seven full rows are shown; the
        // eighth row stays unreachable (T3 will log the overflow count).
        var overflow = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount: 64);
        Check.That(overflow.ShownRows).IsEqualTo(7);
        Check.That(overflow.Capacity).IsEqualTo(63);
        Check.That(overflow.Block.Height).IsEqualTo(7 * TaskSwitcherLayout.CellHeight);

        // Five windows: one row, centred vertically in the space between the band and the hint
        // row (not pinned under the band).
        var few = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 112), "Top", tileCount: 5);
        Check.That(few.ShownRows).IsEqualTo(1);
        Check.That(few.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(60, 546, 1800, 120));
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
        Check.That(at200.Columns).IsEqualTo(9);
        Check.That(at200.Rows).IsEqualTo(7);
    }

    [Fact]
    public void WhenTheKeepOutLeavesNoRow_TheClearanceGapIsDroppedFirst()
    {
        // A 950 DIP top band leaves 102 DIP before the hint row: not one full row with the 24 DIP
        // clearance, but one row (120 DIP) once the gap is dropped. The edge margins stay.
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 950), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(1);
        Check.That(layout.Columns).IsEqualTo(9);
        Check.That(layout.Area.Y).IsEqualTo(950);
        Check.That(layout.Area.X).IsEqualTo(TaskSwitcherLayout.EdgeMargin);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(60, 953, 1800, 120));
    }

    [Fact]
    public void WhenDroppingTheGapStillLeavesNoRow_TheEdgeMarginsAreDroppedNext()
    {
        // 230 DIP of screen: 150 DIP between the edge margins cannot hold a 200 DIP column, but the
        // full 230 DIP can hold one once the margins are dropped.
        var layout = TaskSwitcherLayout.Compute(
            230, ScreenHeight, toolbarBandDip: null, dockedEdge: null, tileCount: 1);

        Check.That(layout.Columns).IsEqualTo(1);
        Check.That(layout.Rows).IsEqualTo(8);
        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(0, 0, 230, 1076));
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(15, 478, 200, 120));
    }

    [Fact]
    public void WhenEvenTheMarginsAreDropped_NoRowFits_TheToolbarKeepOutIsDroppedLast()
    {
        // A 1000 DIP top band leaves 76 DIP above the hint row even with margins and gap gone; the
        // last resort is to ignore the band (the T1 landing guard covers the landing).
        var layout = TaskSwitcherLayout.Compute(
            ScreenWidth, ScreenHeight, Band(0, 0, ScreenWidth, 1000), "Top", tileCount: 63);

        Check.That(layout.Rows).IsEqualTo(8);
        Check.That(layout.Columns).IsEqualTo(9);
        Check.That(layout.Area).IsEqualTo(
            new TaskSwitcherLayout.DipRect(0, 0, ScreenWidth, 1076));
        Check.That(layout.HintBottomMargin).IsEqualTo(0);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(60, 118, 1800, 840));
    }
}
