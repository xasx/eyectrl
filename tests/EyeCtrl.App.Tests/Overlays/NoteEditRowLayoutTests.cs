namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// K2a + A2 (sitting 10): the compact Edit card — text above, the three controls (✓, ⌨, ↩) in
/// ONE row of 112 DIP cells below it — and the bigger note controls (112 DIP cells, 88 DIP discs,
/// a 56 DIP Place-here hit radius). Display, Move and the chips keep the four-row column; every
/// hit area stays centred and symmetric (no gaze correction).
/// </summary>
public class NoteEditRowLayoutTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1200);

    private const double Scale = 2.0;

    private static PhysicalPoint Phys(double dipX, double dipY) => new(dipX * Scale, dipY * Scale);

    private static NotePlacement Place(NoteSide side, double textSize, Size card, double x = 960, double y = 600) =>
        NoteLayout.Place(Phys(x, y), side, card, WorkArea, Array.Empty<Rect>(), Scale)!;

    // ── A2: the final sizes ──

    [Fact]
    public void A2_TheCellDiscAndPlaceHereHitRadius_AreTheSittingTenNumbers()
    {
        Check.WithCustomMessage("every note hit cell is 112 DIP square since A2")
            .That(LayoutConstants.NoteCellDip).IsEqualTo(112.0);
        Check.WithCustomMessage("the drawn disc is 88 DIP across (44 DIP radius)")
            .That(LayoutConstants.NoteButtonRadius * 2).IsEqualTo(88.0);
        Check.WithCustomMessage("the Place-here hit radius is HALF the cell (56 DIP), not the disc radius")
            .That(LayoutConstants.NotePlaceHereHitRadiusDip).IsEqualTo(56.0);
        Check.WithCustomMessage("the control column is padding + cell + padding")
            .That(LayoutConstants.NoteColumnWidthDip).IsEqualTo(132.0);
        Check.WithCustomMessage("A2's Display card floor is 2 x 10 + 4 x 112 = 468 DIP")
            .That(NoteLayout.ControlsColumnHeightDip).IsEqualTo(468.0);
    }

    [Fact]
    public void A2_TheDisc_IsCentredInItsCell_WithEqualAirOnEverySide()
    {
        var cell = LayoutConstants.NoteCellDip;
        double disc = LayoutConstants.NoteButtonRadius * 2;
        double centredOffset = (cell - disc) / 2;

        Check.WithCustomMessage("the disc must sit centred in its cell")
            .That(centredOffset).IsEqualTo(12.0);
        // The row's three cells are symmetric around the card's centre line too: the middle cell
        // is exactly on it and the outer two mirror each other.
        var edit = Place(NoteSide.Right, 36, NoteLayout.EditCardSize(36, 0, WorkArea.Height));
        var (left, _) = NoteLayout.EditCellCenter(edit, 0);
        var (mid, _) = NoteLayout.EditCellCenter(edit, 1);
        var (right, _) = NoteLayout.EditCellCenter(edit, 2);
        Check.WithCustomMessage("the middle cell must sit on the card's centre line")
            .That(mid).IsCloseTo((left + right) / 2, 1e-9);
        Check.WithCustomMessage("the outer cells must mirror each other around it")
            .That(mid - left).IsCloseTo(right - mid, 1e-9);
        Check.WithCustomMessage("the cells must touch (one 112 DIP stride, no drift)")
            .That(mid - left).IsCloseTo(cell, 1e-9);
    }

    // ── K2a: the compact card's shape ──

    [Theory]
    [InlineData(18.0, 356.0, 172.0)] // max(270 text, 336 row) + 20; 10 + 22.5 + 2 + 14.67 + 112 + 10 = 171.17 -> 172
    [InlineData(28.0, 440.0, 184.0)] // max(420, 336) + 20; 10 + 35 + 2 + 14.67 + 112 + 10 = 183.67 -> 184
    [InlineData(36.0, 560.0, 194.0)] // max(540, 336) + 20; 10 + 45 + 2 + 14.67 + 112 + 10 = 193.67 -> 194
    [InlineData(48.0, 740.0, 209.0)] // max(720, 336) + 20; 10 + 60 + 2 + 14.67 + 112 + 10 = 208.67 -> 209
    public void K2a_TheEditCard_IsTextPlusOneRow_NotTheFourRowColumn(double textSize, double width, double height)
    {
        var card = NoteLayout.EditCardSize(textSize, 0, WorkArea.Height);
        Check.WithCustomMessage($"the Edit card at {textSize} DIP must be {width} x {height}")
            .That(card).IsEqualTo(new Size(width, height));
        Check.WithCustomMessage("the compact card must be far under the 468 DIP Display column")
            .That(card.Height).IsStrictlyLessThan(NoteLayout.ControlsColumnHeightDip);
    }

    [Fact]
    public void K2a_TheEditCardHeight_GrowsWithTheDraftAboveTheOneLineFloor()
    {
        double oneLine = NoteLayout.EditCardHeightDip(36, NoteLayout.TextLineHeightDip(36), 1200);
        double threeLines = NoteLayout.EditCardHeightDip(36, 3 * NoteLayout.TextLineHeightDip(36), 1200);

        Check.WithCustomMessage("one line is the floor: an empty draft still holds one line")
            .That(NoteLayout.EditCardHeightDip(36, 0, 1200)).IsEqualTo(oneLine);
        Check.WithCustomMessage("three lines add exactly two more text lines")
            .That(threeLines - oneLine).IsEqualTo(2 * NoteLayout.TextLineHeightDip(36));
        Check.WithCustomMessage("the 3-line Edit card stays compact (under 300 DIP)")
            .That(threeLines).IsStrictlyLessThan(300);
    }

    // ── K2a: the row itself ──

    [Theory]
    [InlineData(NoteSide.Right)]
    [InlineData(NoteSide.Left)]
    [InlineData(NoteSide.Below)]
    [InlineData(NoteSide.Above)]
    public void K2a_TheThreeEditControls_ShareOneCentredRow_BelowTheText(NoteSide side)
    {
        const double textSize = 36;
        var card = NoteLayout.EditCardSize(textSize, NoteLayout.TextLineHeightDip(textSize), WorkArea.Height);
        var placement = Place(side, textSize, card);
        var cardLocal = NoteLayout.RectLocal(placement);
        double cell = LayoutConstants.NoteCellDip;
        double half = cell / 2;

        var done = NoteLayout.ButtonCenter(placement, 0, NoteState.Edit);
        var keyboard = NoteLayout.ButtonCenter(placement, 1, NoteState.Edit);
        var discard = NoteLayout.ButtonCenter(placement, 3, NoteState.Edit);

        Check.WithCustomMessage($"{side}: ✓, ⌨ and ↩ must SHARE one row")
            .That(done.Y).IsCloseTo(keyboard.Y, 1e-9);
        Check.WithCustomMessage($"{side}: ✓, ⌨ and ↩ must SHARE one row")
            .That(discard.Y).IsCloseTo(keyboard.Y, 1e-9);
        Check.WithCustomMessage($"{side}: the row must run left to right ✓, ⌨, ↩")
            .That(done.X).IsStrictlyLessThan(keyboard.X);
        Check.WithCustomMessage($"{side}: the row must run left to right ✓, ⌨, ↩")
            .That(keyboard.X).IsStrictlyLessThan(discard.X);

        // The row sits BELOW the text: its cells' top edge must clear the text block's one line
        // and the end marker's block.
        double textBottom = cardLocal.Top + LayoutConstants.NoteButtonPadding + NoteLayout.TextLineHeightDip(textSize);
        double markerBottom = textBottom + NoteLayout.EditEndMarkerGapDip + NoteLayout.EditEndMarkerHeightDip;
        double rowTop = done.Y - half;
        Check.WithCustomMessage($"{side}: the row (top {rowTop}) must start below the draft and its end marker ({markerBottom})")
            .That(rowTop).IsGreaterOrEqualThan(markerBottom - 1e-9);

        foreach (var centre in new[] { done, keyboard, discard })
        {
            var cellRect = new Rect(centre.X - half, centre.Y - half, cell, cell);
            Check.WithCustomMessage($"{side}: every row cell must lie inside the card {cardLocal}")
                .That(cardLocal.Contains(cellRect)).IsTrue();
        }

        // No control cell may overlap the draft's text area (K2a's whole point: the controls no
        // longer squeeze the text into a narrow column).
        var textArea = new Rect(
            cardLocal.Left + LayoutConstants.NoteButtonPadding,
            cardLocal.Top + LayoutConstants.NoteButtonPadding,
            NoteLayout.TextColumnWidthDip(textSize),
            NoteLayout.TextLineHeightDip(textSize));
        foreach (var centre in new[] { done, keyboard, discard })
        {
            var cellRect = new Rect(centre.X - half, centre.Y - half, cell, cell);
            Check.WithCustomMessage($"{side}: a row cell must never overlap the text {textArea}")
                .That(cellRect.IntersectsWith(textArea)).IsFalse();
        }
    }

    [Fact]
    public void D_DisplayAndMove_UseTheRow_TheChipKeepsTheColumn()
    {
        var reserved = NoteLayout.DisplayReservedSize(0, 0);
        var display = Place(NoteSide.Right, 36,
            NoteLayout.DisplayReservedSize(0, 0));

        // D1/D4/D6: Display (revealed) and Move read the four-cell row under the text.
        Check.WithCustomMessage("Display's slot 0 must read the row")
            .That(NoteLayout.ButtonCenter(display, 0, NoteState.Display))
            .IsEqualTo(NoteLayout.RowCellCenter(display, 0));
        Check.WithCustomMessage("Move's slot 1 must read the row")
            .That(NoteLayout.ButtonCenter(display, 1, NoteState.Move))
            .IsEqualTo(NoteLayout.RowCellCenter(display, 1));
        Check.WithCustomMessage("Display's ✎/↕/✕ share one row (D4)")
            .That(NoteLayout.ButtonCenter(display, 0, NoteState.Display).Y)
            .IsEqualTo(NoteLayout.ButtonCenter(display, 3, NoteState.Display).Y);
        // The chip is the one presentation that keeps the column (its card is not the row card).
        Check.WithCustomMessage("a chip's slot 0 must read the column")
            .That(NoteLayout.ButtonCenter(display, 0, NoteState.PendingUndo))
            .IsEqualTo(NoteLayout.ButtonCenter(display, 0));

        var (rowX, rowY) = NoteLayout.ButtonCenter(display, 0, NoteState.Display);
        var (colX, colY) = NoteLayout.ButtonCenter(display, 0);
        Check.WithCustomMessage("the row must not be the chip's column cell")
            .That(rowX == colX && rowY == colY).IsFalse();

        // The row's four cells are inside the reserved card at every size.
        foreach (var size in new[] { 18.0, 28.0, 36.0, 48.0 })
        {
            var p = Place(NoteSide.Right, size, NoteLayout.DisplayReservedSize(0, 0));
            var card = NoteLayout.RectLocal(p);
            for (int cell = 0; cell < 4; cell++)
            {
                var (cx, cy) = NoteLayout.RowCellCenter(p, cell);
                var cellRect = new Rect(
                    cx - (LayoutConstants.NoteCellDip / 2), cy - (LayoutConstants.NoteCellDip / 2),
                    LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
                Check.WithCustomMessage($"{size}: row cell {cell} must lie inside the reserved card")
                    .That(card.Contains(cellRect)).IsTrue();
            }
        }
        Check.That(reserved.Width).IsEqualTo(4 * LayoutConstants.NoteCellDip + 2 * LayoutConstants.NoteButtonPadding);
    }

    [Fact]
    public void K2a_TheEditTextArea_StopsAboveTheRow()
    {
        var card = NoteLayout.EditCardSize(36, 0, WorkArea.Height);
        double area = NoteLayout.EditTextAreaHeightDip(card.Height, 36);

        Check.WithCustomMessage("the text area must hold at least one 36 DIP line's leading")
            .That(area).IsGreaterOrEqualThan(NoteLayout.TextLineHeightDip(36));
        Check.WithCustomMessage("the rounded-up card must not hand the area a whole extra line")
            .That(area).IsStrictlyLessThan(NoteLayout.TextLineHeightDip(36) + 1);
        Check.WithCustomMessage("the area must exclude the row and the marker block")
            .That(area + (2 * LayoutConstants.NoteButtonPadding) + NoteLayout.EditEndMarkerGapDip
                + NoteLayout.EditEndMarkerHeightDip + LayoutConstants.NoteCellDip).IsCloseTo(card.Height, 1e-9);
    }

    [Fact]
    public void K2a_TheEditCardAtOneLineAndThreeLines_IsWhatTheTicketPromised()
    {
        // The ticket's "about 150-170 DIP for one or two lines" was computed with the OLD 80 DIP
        // cell; A2's 112 DIP row adds 32. These are the real numbers: 194 at one line and 284 at
        // three, at the 36 DIP default with the tag's 22 DIP protrusion on the drawn window.
        var one = NoteLayout.EditCardSize(36, NoteLayout.TextLineHeightDip(36), 1200);
        var three = NoteLayout.EditCardSize(36, 3 * NoteLayout.TextLineHeightDip(36), 1200);
        Check.That(one.Height).IsEqualTo(194.0);
        Check.That(three.Height).IsEqualTo(284.0);
        Check.WithCustomMessage("the drawn window adds the tag's protrusion")
            .That(one.Height + NoteLayout.TagProtrusionDip(36)).IsEqualTo(216.0);
    }
}
