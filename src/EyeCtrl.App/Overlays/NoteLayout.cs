namespace EyeCtrl.App.Overlays;

using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;


/// <summary>
/// One derived note placement (P3 D2): the side the card sits on and every rect the window,
/// the manager's dwell targets and the chrome zones need. All values are DIP in SCREEN
/// coordinates, except the tail triangle which <see cref="TailTriangle"/> returns in
/// window-relative coordinates ready to draw. <see cref="TailTip"/> is null exactly when the
/// tail is hidden — the clamped fallback where the anchor ended up under the card.
/// </summary>
/// <param name="Side">The side this placement chose.</param>
/// <param name="Window">The full window rect: card plus the tail zone on the anchor side.</param>
/// <param name="Card">The card body rect (what the title bar, text and controls fill).</param>
/// <param name="TailTip">The tail tip in screen DIP, <see cref="Constants.TipGapDip"/> short of
/// the anchor — or null when the tail is hidden.</param>
internal sealed record NotePlacement(NoteSide Side, Rect Window, Rect Card, Point? TailTip)
{
    /// <summary>Whether the pointer tail is drawn (P3 D2.3: hidden when the anchor is under the card).</summary>
    public bool TailVisible => TailTip is not null;
}

/// <summary>
/// Single source of truth for note geometry (P3): the pure <see cref="Place"/> function derives
/// every window rect from the anchor, the side, the card size, the work area and the avoid
/// rects — nothing chases the gaze and nothing is stored on the note except its anchor and side.
/// The per-side button-column math (<see cref="ButtonCenter(NoteSide, int)"/>) is shared by
/// <see cref="NoteWindow"/> (which draws the controls) and <c>NoteManager</c> (which places the
/// dwell targets over them), so the two cannot drift apart.
/// </summary>
internal static class NoteLayout
{
    /// <summary>The fixed side cycle (P3 D2.1): preferred first, then this order.</summary>
    private static readonly NoteSide[] Cycle = [NoteSide.Right, NoteSide.Left, NoteSide.Below, NoteSide.Above];

    /// <summary>Window-relative X of the hit-cell column centre when the column sits on the
    /// card's RIGHT edge (the side away from the tail for Right/Below/Above): the CARD width
    /// minus the padding minus the half-cell (T5 review-01 B1 - the width dropped out of this
    /// formula once and put the column over the body text).</summary>
    public static double ColumnCenterX(double cardWidthDip) =>
        cardWidthDip - LayoutConstants.NoteButtonPadding - (LayoutConstants.NoteCellDip / 2);

    /// <summary>Window-relative X of the hit-cell column centre when the column sits on the card's
    /// LEFT edge - the side away from the tail on a <see cref="NoteSide.Left"/> placement.</summary>
    public static double ColumnCenterXFromLeft =>
        LayoutConstants.NoteButtonPadding + (LayoutConstants.NoteCellDip / 2);


    /// <summary>Window-relative Y of hit-cell row <paramref name="row"/>'s centre (P4 D3): the
    /// rows are the four stacked 80 DIP cells between the column paddings.</summary>
    public static double RowCenterY(int row) =>
        LayoutConstants.NoteButtonPadding + (row * LayoutConstants.NoteCellDip) + (LayoutConstants.NoteCellDip / 2);

    // == the number tag (T6 fixup-01; fixup-04: the whole block scales with the body text) ==

    /// <summary>The tag numeral's font size as a fraction of the body text size (review-02 F1 /
    /// owner decision fixup-04): 0.75, so the numeral's ink reads near the body capital at every
    /// T8 size — 13.5/21/36 DIP at 18/28/48, next to a body cap of about 19.6 DIP at 28.</summary>
    private const double TagNumeralScale = 0.75;

    /// <summary>The tag's vertical padding above and below the numeral ink.</summary>
    public const double TagVerticalPadDip = 4.0;

    /// <summary>The tag's fixed in-card DEPTH: how far it rides INTO the card below its top
    /// edge. The body text's top margin is 10 DIP, so the first text line stays clear whatever
    /// the tag's height grows to; the extra height of a bigger text size goes UP instead.</summary>
    public const double TagInsetDepthDip = 9.0;

    /// <summary>The tag's minimum width: at the smallest text size the derived height drops
    /// under what a straddling tag needs to read as its own piece, so the floor keeps the box
    /// proportionate to the disc column beside it.</summary>
    public const double TagMinWidthDip = 28.0;

    /// <summary>The tag numeral's font size in DIP: 0.75 × the body text size.</summary>
    public static double TagNumeralFontDip(double textSizeDip) => TagNumeralScale * textSizeDip;

    /// <summary>The tag numeral's INK height at its drawn font: the bounding box of the tallest
    /// digit the tag can paint. Height and width derive from this measured ink, so the drawn
    /// label and the derived geometry cannot drift apart. Measured per text size and memoized —
    /// the FormattedText/BuildGeometry walk is not free and every placement re-derivation reads it.</summary>
    public static double TagNumeralInkHeightDip(double textSizeDip)
    {
        lock (_tagInkCache)
        {
            if (_tagInkCache.TryGetValue(textSizeDip, out double ink))
                return ink;
        }

        var formatted = new FormattedText(
            "0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), TagNumeralFontDip(textSizeDip),
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        // All ten digits sit on one shared baseline, so the union box HEIGHT is the tallest
        // digit's ink — the height the numeral box must hold for any number.
        double height = formatted.BuildGeometry(new Point(0, 0)).Bounds.Height;
        lock (_tagInkCache)
        {
            _tagInkCache[textSizeDip] = height;
        }
        return height;
    }

    /// <summary>The tag's height: the numeral ink plus the padding above and below, rounded UP
    /// to a whole DIP (T6 fixup-05, review-04 F1). The window's origin snaps to whole pixels
    /// and the canvas draws the card <see cref="TagProtrusionDip(double)"/> below it, so a
    /// fractional height put the whole note — the card rim, the cells, the discs, the text and
    /// the tag — on a fractional pixel phase and every straight edge went soft on the owner's
    /// 2.0 display. The ceil costs at most 1 DIP of padding (invisible) and makes the height,
    /// the width and the protrusion whole DIPs again.</summary>
    public static double TagHeightDip(double textSizeDip) =>
        Math.Ceiling(TagNumeralInkHeightDip(textSizeDip) + (2 * TagVerticalPadDip));

    /// <summary>The tag's width: never below the floor, otherwise the height (a near-square box
    /// around one numeral).</summary>
    public static double TagWidthDip(double textSizeDip) =>
        Math.Max(TagMinWidthDip, TagHeightDip(textSizeDip));

    /// <summary>How much of the tag rides ABOVE the card's top edge (the tag straddles it): the
    /// tag height minus the fixed in-card depth. The window grows by exactly this at the top
    /// when the tag shows — the card's screen rect is untouched, and Place's 64 DIP work-area
    /// inset guarantees the strip stays inside the work area even for a clamped placement (the
    /// largest growth, at the 48 DIP text, is under 30 DIP).</summary>
    public static double TagProtrusionDip(double textSizeDip) =>
        TagHeightDip(textSizeDip) - TagInsetDepthDip;

    /// <summary>The tag ink heights, memoized per text size (see <see cref="TagNumeralInkHeightDip"/>).</summary>
    private static readonly Dictionary<double, double> _tagInkCache = [];

    /// <summary>The tag's inset from the card's text-side top corner.</summary>
    public const double TagCornerInsetDip = 12.0;

    /// <summary>Clearance the tag keeps from the tail base when it slides along the top edge.</summary>
    public const double TagTailClearanceDip = 6.0;

    /// <summary>
    /// Grows the placement's window upward by the tag's protrusion (fixup-04: the growth
    /// follows the body text size, <see cref="TagProtrusionDip(double)"/>) so the tag can
    /// straddle the card's top edge. The CARD rect — and therefore every screen position, the
    /// text and all hit cells — is untouched; only the window's top rises and its height grows.
    /// Idempotent: a placement that already carries the clearance passes through unchanged.
    /// </summary>
    /// <param name="placement">The raw derivation's placement.</param>
    /// <param name="textSizeDip">The body text size the tag sits beside — the setting's
    /// <c>TextSizeDip</c> at derivation time, so a T8 re-derivation re-sizes the strip.</param>
    public static NotePlacement WithTagClearance(NotePlacement placement, double textSizeDip)
    {
        if (ExtraTopClearance(placement) > 0)
            return placement; // already grown
        double protrusion = TagProtrusionDip(textSizeDip);
        var w = placement.Window;
        return placement with { Window = new Rect(w.Left, w.Top - protrusion, w.Width, w.Height + protrusion) };
    }

    /// <summary>How far the placement's card sits below the STATIC per-side window origin
    /// (<see cref="CardOrigin"/>) — zero for every placement Place derives, and
    /// <see cref="TagProtrusionDip(double)"/> once <see cref="WithTagClearance"/> grew the window.
    /// The tag clearance rides every window-relative placement computation (target centres,
    /// drawn buttons) so a grown window draws and registers at the SAME screen positions.</summary>
    public static double ExtraTopClearance(NotePlacement placement)
    {
        var (_, originY) = CardOrigin(placement.Side);
        return (placement.Card.Top - placement.Window.Top) - originY;
    }

    /// <summary>
    /// The drawn tag's rect in window-relative coordinates (fixup-04: sized from the body text
    /// size — the numeral's 0.75× font sets the height, and the window grew for the matching
    /// protrusion): straddling the card's top edge at the TEXT-side end — the end away from the
    /// control column, which hugs the edge away from the tail — inset from the corner. The
    /// in-card depth stays fixed at <see cref="TagInsetDepthDip"/>, so the first text line stays
    /// clear whatever the height; the extra height of a larger text size rides above the edge.
    /// On a Below placement the tail shares the top edge, so the tag slides along it toward the
    /// column (never over it) until it clears the tail polygon's base.
    /// </summary>
    public static Rect TagRect(NotePlacement placement, double textSizeDip)
    {
        var card = RectLocal(placement);
        bool textOnRight = placement.Side == NoteSide.Left;
        double width = TagWidthDip(textSizeDip);
        double height = TagHeightDip(textSizeDip);
        double x = textOnRight
            ? card.Right - TagCornerInsetDip - width
            : card.Left + TagCornerInsetDip;
        double y = card.Top - TagProtrusionDip(textSizeDip);

        if (placement.Side == NoteSide.Below && TailTriangle(placement) is { } tri)
        {
            // The tail base sits on the card's top edge between the triangle's 2nd/3rd points.
            double baseLeft = Math.Min(tri[1].X, tri[2].X) - TagTailClearanceDip;
            double baseRight = Math.Max(tri[1].X, tri[2].X) + TagTailClearanceDip;
            bool overlaps = x < baseRight && x + width > baseLeft;
            if (overlaps)
            {
                // Slide toward the column so the tag's LEFT edge clears the base's right end.
                x = baseRight;
                // Never over the column: the tag stops short of the column strip.
                double limit = card.Right - LayoutConstants.NoteColumnWidthDip - width;
                if (x > limit) x = limit;
            }
        }
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// The per-state slot table (P4 D3). A slot IS a hit-cell row: slot 0 and 1 are adjacent,
    /// slot 2 is the full empty gap cell (P2 D8's "one cell apart", P1 D7's "Discard one slot
    /// from Done"), and slot 3 is the bottom row. Display/Edit occupy rows 0, 1 and 3; Move
    /// occupies rows 0 and 1; a chip occupies row 0 only (the Undo cell).
    /// </summary>
    public static IReadOnlyList<int> SlotsFor(NoteState state) => state switch
    {
        NoteState.Display or NoteState.Edit => [0, 1, 3],
        NoteState.Move => [0, 1],
        NoteState.PendingUndo => [0],
        _ => Array.Empty<int>(),
    };

    /// <summary>
    /// Window-relative centre of hit cell #<paramref name="slot"/> for a card on
    /// <paramref name="side"/> (P3 D2.6 + P4 D3): the column sits on the card edge AWAY from
    /// the tail - the far vertical edge for Right/Left, the right edge for Below/Above - and
    /// the slot maps 1:1 to its hit-cell row. The card origin inside the window shifts by the
    /// tail zone, so the returned point is window-relative, directly usable on the window's
    /// canvas and by <c>NoteManager</c>'s dwell-target placement.
    /// <para>
    /// T6 fixup-01: <paramref name="extraTopClearanceDip"/> rides the number tag's window
    /// growth (<see cref="ExtraTopClearance"/>) — a grown window's card sits lower in the
    /// window by the protrusion, and the drawn cells and registered targets must follow so
    /// every screen position stays byte-for-byte where the ungrown world put it.
    /// </para>
    /// </summary>
    public static (double X, double Y) ButtonCenter(NoteSide side, int slot, double cardWidthDip,
        double extraTopClearanceDip = 0)
    {
        double colX = side == NoteSide.Left ? ColumnCenterXFromLeft : ColumnCenterX(cardWidthDip);
        var (originX, originY) = CardOrigin(side);
        return (originX + colX, originY + extraTopClearanceDip + RowCenterY(slot));
    }

    /// <summary>Window-relative centre of hit cell #<paramref name="slot"/> for a stored
    /// placement (T6 fixup-01): the ONE shape every read-from-the-placement caller uses, so the
    /// drawn cells, the dwell targets and the chrome rects all carry the tag's top-clearance
    /// ride and can never disagree with the drawn geometry over a grown window.</summary>
    public static (double X, double Y) ButtonCenter(NotePlacement placement, int slot) =>
        ButtonCenter(placement.Side, slot, placement.Card.Width, ExtraTopClearance(placement));


    // == the card size (P4 D2): the text size drives the width, the measured text height
    // (capped) drives the height, and the control column is the floor. ==

    /// <summary>The text column width in DIP: round(15 x size), about 28-32 characters per line.
    /// 420 DIP at the provisional 28.</summary>
    public static double TextColumnWidthDip(double textSize) => Math.Round(15 * textSize);

    /// <summary>The card width in DIP: the text column plus the control column.</summary>
    /// <summary>The strip between the text column and the hit-cell column (T5 review-01 B1):
    /// the text never touches the cells - WPF rect intersection is edge-inclusive, so a real
    /// gap is the only honest clearance.</summary>
    public const double TextColumnGapDip = 30.0;

    public static double CardWidthDip(double textSize) =>
        TextColumnWidthDip(textSize) + LayoutConstants.NoteColumnWidthDip + TextColumnGapDip;

    /// <summary>The control column's height in DIP (P4 D3): four hit-cell rows between the paddings.</summary>
    public static double ControlsColumnHeightDip =>
        (2 * LayoutConstants.NoteButtonPadding) + (4 * LayoutConstants.NoteCellDip);

    /// <summary>One text line's height in DIP: the 1.25x leading of P4 D2.</summary>
    public const double TextLineHeightFactor = 1.25;

    public static double TextLineHeightDip(double textSize) => textSize * TextLineHeightFactor;

    /// <summary>The auto-height cap (P4 D2): the smaller of 55 percent of the work area height
    /// and 12 text lines. Over the cap the card stops growing and the text clips (fade + ellipsis).</summary>
    public static double TextCapHeightDip(double workAreaHeightDip, double textSize) =>
        Math.Min(workAreaHeightDip * 0.55, 12 * TextLineHeightDip(textSize));

    /// <summary>
    /// The card height in DIP (P4 D2): the larger of the control column (the floor - the cells
    /// must always fit) and the measured text height plus padding, never past the cap.
    /// </summary>
    public static double CardHeightDip(double textSize, double measuredTextHeightDip, double workAreaHeightDip)
    {
        double cap = TextCapHeightDip(workAreaHeightDip, textSize);
        double content = Math.Min(measuredTextHeightDip, cap);
        return Math.Max(ControlsColumnHeightDip, content + (2 * LayoutConstants.NoteButtonPadding));
    }

    // == the chip (P4 D3's PendingUndo row): the shrunk-in-place window renders a single Undo
    // cell plus the chip text. ==

    /// <summary>The chip text's font size in DIP (T6 fixup-06 G1): the NOTE's body text size.
    /// 12 DIP - <c>Tokens.FontSizeBody</c> - was the size P4 removed from the note body (P4 D2);
    /// the chip kept it, and on the owner's 2.0 display the "Note removed - Undo" line was
    /// unreadable from arm's length. The chip is the ONLY affordance the caretaker has in the
    /// PendingUndo state, so it reads at the note's own size.</summary>
    public static double ChipTextFontDip(double textSizeDip) => textSizeDip;

    /// <summary>The chip text's margin in DIP (T6 fixup-06 G1): the inset the drawn chip
    /// text keeps inside the card. The card's height is derived from it, so the margin is a
    /// shared constant and not a literal in the window.</summary>
    public const double ChipTextMarginDip = 10.0;

    /// <summary>
    /// The chip's line with a NO-BREAK space before the middot (T6 fixup-07, the reviewer's
    /// recommendation): a wrapped line that starts with "·" reads like a bullet, so the dot
    /// stays on the line it belongs to.
    /// <para>
    /// Applied in CODE, to BOTH the drawn chip text and the texts the column is measured from,
    /// so the two can never disagree. The strings themselves are untouched: editing
    /// <c>strings*.json</c> would re-hash every German entry and pull in the l10n team. The
    /// measurement splits words on ' ' only, so the no-break space keeps "verworfen" and the middot one word.
    /// </para>
    /// </summary>
    public static string NoBreakBeforeDot(string text) =>
        text.Replace(" · ", "\u00A0· ");

    /// <summary>The chip card's minimum height in DIP: one hit-cell row between the paddings -
    /// the Undo cell must fit whatever the text needs.</summary>
    public static double ChipCellRowHeightDip =>
        (2 * LayoutConstants.NoteButtonPadding) + LayoutConstants.NoteCellDip;

    /// <summary>
    /// The chip text column's width in DIP (T6 fixup-06 G1): MEASURED, never guessed.
    /// <para>
    /// The column is the narrowest width at which EVERY one of <paramref name="texts"/> wraps
    /// into at most two lines at the chip's font size, measured with the real typeface - and
    /// never narrower than the widest single WORD among them, because a word never breaks and
    /// an over-narrow column would let it overflow instead of wrap. The chip's two texts are
    /// kind-dependent ("Note removed - Undo" and "Changes discarded - Undo", plus their German),
    /// so the width is the MAX over all of them: the column that fits the longest is the one
    /// that fits every one.
    /// </para>
    /// <para>
    /// The old 140 DIP was a guess that only held at 12 DIP. At the note's own size the same
    /// 140 DIP forced three lines, and the third line fell out of the card.
    /// </para>
    /// </summary>
    /// <param name="textSizeDip">The chip's font size - the note's body text size.</param>
    /// <param name="texts">Every chip text the column must hold, in every shipped language.</param>
    public static double ChipTextWidthDip(double textSizeDip, IReadOnlyList<string> texts) =>
        MeasureChip(textSizeDip, texts).Width;

    /// <summary>
    /// The chip text's drawn height in DIP (T6 fixup-06 G1): the lines the texts actually take
    /// at the measured column width, at the note's 1.25x leading. The card grows to hold it.
    /// </summary>
    /// <param name="textSizeDip">The chip's font size - the note's body text size.</param>
    /// <param name="texts">Every chip text the column must hold, in every shipped language.</param>
    public static double ChipTextHeightDip(double textSizeDip, IReadOnlyList<string> texts) =>
        MeasureChip(textSizeDip, texts).Height;

    /// <summary>
    /// The chip card's size in DIP (T6 fixup-06 G1): the text column plus the Undo column
    /// across, and the taller of one hit-cell row and the text's own height plus its margin
    /// down. The card grows with the note's text size, so the chip is size-DEPENDENT (R12's
    /// old "size-independent" no longer holds - see <c>NoteManager</c>'s captured chip size).
    /// </summary>
    /// <param name="textSizeDip">The chip's font size - the note's body text size.</param>
    /// <param name="texts">Every chip text the column must hold, in every shipped language.</param>
    public static Size ChipCardSize(double textSizeDip, IReadOnlyList<string> texts)
    {
        var metrics = MeasureChip(textSizeDip, texts);
        return new Size(
            metrics.Width + LayoutConstants.NoteColumnWidthDip,
            Math.Max(ChipCellRowHeightDip, metrics.Height + ChipTextMarginDip));
    }

    /// <summary>The measured chip metrics: the text column's width and the text's drawn height.</summary>
    private readonly record struct ChipMetrics(double Width, double Height);

    /// <summary>The measured chip column widths per (text, font size), memoized: the
    /// FormattedText walk and the binary search are not free, and every chip re-derivation
    /// reads them.</summary>
    private static readonly Dictionary<(string Text, double FontSize), double> _chipWidthCache = new();

    private static ChipMetrics MeasureChip(double textSizeDip, IReadOnlyList<string> texts)
    {
        double fontSize = ChipTextFontDip(textSizeDip);
        double width = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            width = Math.Max(width, MeasuredColumnWidthDip(text, fontSize));
        }

        double height = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            height = Math.Max(height, WrappedLineCount(text, fontSize, width) * TextLineHeightDip(textSizeDip));
        }

        // WHOLE DIPs (T6 fixup-05's rule, the same reason the tag's height is rounded up): a
        // fractional chip width puts the window's edge on a fractional pixel phase, and WPF
        // snaps the window's bounds to whole device pixels - so the drawn chip and the placement
        // the dwell targets were registered from would differ. The ceiling keeps the text
        // inside the column and the geometry on the pixel grid.
        return new ChipMetrics(Math.Ceiling(width), Math.Ceiling(height));
    }

    /// <summary>
    /// The narrowest column width in DIP at which <paramref name="text"/> wraps into at most
    /// two lines at <paramref name="fontSize"/> - and never narrower than its widest word.
    /// Measured with the real typeface, not estimated from a character count.
    /// </summary>
    private static double MeasuredColumnWidthDip(string text, double fontSize)
    {
        lock (_chipWidthCache)
        {
            if (_chipWidthCache.TryGetValue((text, fontSize), out double cached))
                return cached;
        }

        double measured = MeasureColumnWidthDip(text, fontSize);
        lock (_chipWidthCache)
        {
            _chipWidthCache[(text, fontSize)] = measured;
        }
        return measured;
    }

    /// <summary>
    /// The narrowest column width in DIP at which <paramref name="text"/> wraps into at most
    /// two lines at <paramref name="fontSize"/> - and never narrower than its widest word.
    /// Measured with the real typeface, not estimated from a character count.
    /// </summary>
    private static double MeasureColumnWidthDip(string text, double fontSize)
    {
        double natural = NaturalTextWidthDip(text, fontSize);
        if (natural <= 0) return 1;

        // The widest single word: a word never breaks, so a column narrower than the word lets
        // it overflow instead of wrap, and the chip's text would leave its column.
        double widestWord = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            widestWord = Math.Max(widestWord, NaturalTextWidthDip(word, fontSize));

        // Binary search on the narrowest width that still fits two lines: the line count never
        // grows with the width, so the predicate is monotone.
        double lo = 1, hi = natural;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (mid <= lo || mid >= hi) break;
            if (WrappedLineCount(text, fontSize, mid) <= 2) hi = mid; else lo = mid;
        }
        return Math.Max(hi, widestWord);
    }

    /// <summary>The text's natural (unwrapped) width in DIP at <paramref name="fontSize"/>.</summary>
    private static double NaturalTextWidthDip(string text, double fontSize)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), fontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>How many lines <paramref name="text"/> wraps into at
    /// <paramref name="maxWidth"/> DIP and <paramref name="fontSize"/> DIP.</summary>
    private static int WrappedLineCount(string text, double fontSize, double maxWidth)
    {
        if (maxWidth <= 0) return int.MaxValue;
        var oneLine = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), fontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        if (oneLine.Height <= 0) return 1;
        var wrapped = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), fontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0)
        { MaxTextWidth = maxWidth };
        return (int)Math.Round(wrapped.Height / oneLine.Height);
    }

    // == the capacity notice (P4 D4): one fixed-size, click-through, target-free window. ==

    /// <summary>The notice card is 320x110 DIP: the EN and DE sentences on two wrapped lines
    /// at the body-large size, with 16 DIP padding all around.</summary>
    public const double NoticeCardWidthDip = 320.0;

    public const double NoticeCardHeightDip = 110.0;
    /// <summary>Where the card rect sits inside the window, per side: the tail zone occupies the
    /// strip between the window edge and the card on the anchor's side.</summary>
    public static (double X, double Y) CardOrigin(NoteSide side) => side switch
    {
        NoteSide.Right => (Constants.TailLengthDip, 0),
        NoteSide.Left => (0, 0),
        NoteSide.Below => (0, Constants.TailLengthDip),
        _ => (0, 0) // Above
    };

    /// <summary>
    /// Derives the placement for one anchor (P3 D2). Pure: reads nothing but its arguments.
    /// <para>
    /// Candidates: <paramref name="preferred"/> first, then the fixed cycle, skipping
    /// <paramref name="exclude"/>. A side fits when the card plus tail (the whole window) lies
    /// inside the work area inset by <see cref="Constants.EdgeMarginDip"/> and intersects no
    /// avoid rect; the first fit wins. With no fit and no <paramref name="exclude"/>, the
    /// preferred side's window is clamped inside the inset work area — the note is never
    /// off-screen, and the tail bends to the anchor or hides when the anchor ends up under the
    /// card. With <paramref name="exclude"/>, a miss returns NULL: the caller (the Move state's
    /// "Other side") must not offer a side that does not exist.
    /// </para>
    /// <para>
    /// T6 fixup-06 G2: with <paramref name="allowDisplaced"/> (the Edit state only) and no
    /// fitting side, the windows that STEP CLEAR of an avoid rect are tried before the clamp
    /// fallback: the same card shifted just outside the rect it hits, by the clearance gap, so a
    /// note whose anchor lies under the keyboard lands beside it instead of on it. The closest
    /// accepted candidate to the anchor wins, ties broken by the side order. With no accepted
    /// candidate the old clamp fallback stands, unchanged.
    /// </para>
    /// </summary>
    /// <param name="anchor">The anchor in physical pixels; converted to DIP once, here.</param>
    /// <param name="preferred">The side tried first.</param>
    /// <param name="cardDip">The card size in DIP (today's fixed card; T5 keeps this the input).</param>
    /// <param name="workAreaDip">The work area in DIP (injected; production: SystemParameters.WorkArea).</param>
    /// <param name="avoidDip">Rects the window must not intersect, in DIP (Edit: keyboard + edge bar).</param>
    /// <param name="scale">The DPI scale that converts the physical anchor into DIP.</param>
    /// <param name="exclude">A side never to choose, or null.</param>
    /// <param name="allowDisplaced">Whether the Edit rules apply: the displaced candidates may
    /// be used, AND the 24 DIP clearance holds in pass 1 too. The EDIT state alone opts in: the
    /// Move state's "Other side" excludes a side and must offer one that exists, and Display,
    /// Move and the chips never want a displaced window.</param>
    /// <returns>The placement, or null only when <paramref name="exclude"/> removed the only
    /// fitting side.</returns>
    public static NotePlacement? Place(
        PhysicalPoint anchor,
        NoteSide preferred,
        Size cardDip,
        Rect workAreaDip,
        IReadOnlyList<Rect> avoidDip,
        double scale,
        NoteSide? exclude = null,
        bool allowDisplaced = false)
    {
        var anchorDip = new Point(anchor.X / scale, anchor.Y / scale);
        var inset = Inset(workAreaDip, Constants.EdgeMarginDip);

        // Pass 1: a side that fits clear of every avoid rect wins outright.
        foreach (var side in Candidates(preferred, exclude))
        {
            var window = WindowFor(anchorDip, side, cardDip);
            // Display, Move and the chips: exactly the placements they always had.
            if (!FitsInside(window, inset))
                continue;
            if (IntersectsAny(window, avoidDip))
                continue;
            return Build(side, window, cardDip, anchorDip, clamped: false);
        }

        // Pass 2 (G2): no side fits clear, so the windows that STEP CLEAR of the rect they hit
        // are tried. The closest accepted candidate to the anchor wins; the side order breaks
        // a tie, so the walk order is the tie-break order.
        // The Edit state is the ONLY caller that opts in: the one call site that excludes a
        // side (the Move state's "Other side") must offer a side that EXISTS, never a displaced
        // one, so it never opts in - the "exclude is null" precondition the ticket names.
        if (allowDisplaced && avoidDip.Count > 0)
        {
            NotePlacement? best = null;
            double bestDistance = double.MaxValue;
            foreach (var side in Candidates(preferred, exclude))
            {
                var window = WindowFor(anchorDip, side, cardDip);
                if (!IntersectsAny(window, avoidDip))
                    continue; // pass 1 would have taken it
                foreach (var displaced in DisplacedCandidates(window, avoidDip, inset))
                {
                    if (!FitsInside(displaced, inset))
                        continue;
                    if (!ClearsEvery(displaced, avoidDip))
                        continue;
                    double distance = DistanceToRect(anchorDip, displaced);
                    if (best is not null && distance >= bestDistance)
                        continue; // a tie keeps the earlier side
                    bestDistance = distance;
                    best = Build(side, displaced, cardDip, anchorDip, clamped: true);
                }
            }
            if (best is not null)
                return best;
        }

        if (exclude is not null)
            return null; // P3 D2: null only when exclude removed the only fitting side

        // No fit at all: clamp the preferred side's window inside the inset work area (P3 D2.3).
        var clamped = WindowFor(anchorDip, preferred, cardDip);
        clamped = ClampInto(clamped, inset);
        return Build(preferred, clamped, cardDip, anchorDip, clamped: true);
    }

    /// <summary>
    /// The windows that step clear of the avoid rects (T6 fixup-06 G2): for every rect the
    /// window hits, the same window shifted just outside it - below, above, right or left - by
    /// the clearance gap, then clamped into the inset work area. A shift that does not clear
    /// every rect is rejected by the caller.
    /// <para>
    /// T6 fixup-07 F2: the shift is computed on the DRAWN window - the one grown upward by the
    /// number tag's strip - so the tag comes with it and the clearance holds for the tag and the
    /// card alike. The "below" shift therefore starts the raw window the tag's growth lower.
    /// </para>
    /// </summary>
    private static IEnumerable<Rect> DisplacedCandidates(Rect window, IReadOnlyList<Rect> avoid, Rect inset)
    {
        double gap = Constants.AvoidGapDip;
        foreach (var rect in avoid)
        {
            if (!window.IntersectsWith(rect))
                continue;
            // Below the rect.
            yield return ClampInto(new Rect(window.Left, rect.Bottom + gap, window.Width, window.Height), inset);
            // Above the rect.
            yield return ClampInto(new Rect(window.Left, rect.Top - gap - window.Height, window.Width, window.Height), inset);
            // Right of the rect.
            yield return ClampInto(new Rect(rect.Right + gap, window.Top, window.Width, window.Height), inset);
            // Left of the rect.
            yield return ClampInto(new Rect(rect.Left - gap - window.Width, window.Top, window.Width, window.Height), inset);
        }
    }

    /// <summary>Whether the window keeps the clearance gap from EVERY avoid rect (G2): a note
    /// control never sits within a key's reach of the keyboard's edge.</summary>
    private static bool ClearsEvery(Rect window, IReadOnlyList<Rect> avoid)
    {
        foreach (var rect in avoid)
        {
            if (!KeepsGapFrom(window, rect, Constants.AvoidGapDip))
                return false;
        }
        return true;
    }

    /// <summary>Whether the window keeps at least <paramref name="gap"/> DIP from the rect on
    /// every side: the window lies wholly beyond the rect, inflated by the gap. Touching the
    /// inflated edge counts as clearing - the gap is a MINIMUM, not an exact distance.</summary>
    private static bool KeepsGapFrom(Rect window, Rect rect, double gap)
    {
        if (window.Right <= rect.Left - gap) return true;
        if (window.Left >= rect.Right + gap) return true;
        if (window.Bottom <= rect.Top - gap) return true;
        if (window.Top >= rect.Bottom + gap) return true;
        return false;
    }

    /// <summary>The distance from a point to a rect: zero inside it, otherwise the distance to
    /// the nearest point of the rect (G2's "closest to the anchor" measure).</summary>
    private static double DistanceToRect(Point point, Rect rect)
    {
        double dx = Math.Max(rect.Left - point.X, 0) + Math.Max(point.X - rect.Right, 0);
        double dy = Math.Max(rect.Top - point.Y, 0) + Math.Max(point.Y - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The tail triangle in window-relative points (tip first), or null when the tail is
    /// hidden. The base sits on the card edge facing the anchor, half-width
    /// <see cref="Constants.TailHalfWidthDip"/> around the tip — the tip's Y (or X, on vertical
    /// sides) was clamped into the card when the placement was built, so the base is always
    /// inside the card and the triangle inside the window's tail zone.</summary>
    public static Point[]? TailTriangle(NotePlacement placement)
    {
        if (placement.TailTip is not { } tip)
            return null;

        var origin = CardOrigin(placement.Side);
        var local = new Point(tip.X - placement.Window.X, tip.Y - placement.Window.Y);
        var card = RectLocal(placement);
        double hw = Constants.TailHalfWidthDip;

        return placement.Side switch
        {
            // Tail on the card's LEFT edge, pointing left toward the anchor.
            NoteSide.Right =>
            [
                local,
                new(card.Left, Clamp(local.Y - hw, card.Top, card.Bottom)),
                new(card.Left, Clamp(local.Y + hw, card.Top, card.Bottom)),
            ],
            // Tail on the card's RIGHT edge, pointing right.
            NoteSide.Left =>
            [
                local,
                new(card.Right, Clamp(local.Y - hw, card.Top, card.Bottom)),
                new(card.Right, Clamp(local.Y + hw, card.Top, card.Bottom)),
            ],
            // Tail on the card's TOP edge, pointing up.
            NoteSide.Below =>
            [
                local,
                new(Clamp(local.X - hw, card.Left, card.Right), card.Top),
                new(Clamp(local.X + hw, card.Left, card.Right), card.Top),
            ],
            // Tail on the card's BOTTOM edge, pointing down.
            _ =>
            [
                local,
                new(Clamp(local.X - hw, card.Left, card.Right), card.Bottom),
                new(Clamp(local.X + hw, card.Left, card.Right), card.Bottom),
            ],
        };
    }

    /// <summary>The placement's card rect in window-relative coordinates (what the window's
    /// canvas draws the card body at).</summary>
    public static Rect RectLocal(NotePlacement placement) => new(
        placement.Card.X - placement.Window.X,
        placement.Card.Y - placement.Window.Y,
        placement.Card.Width,
        placement.Card.Height);

    // ── the pure pieces of Place ──

    private static IEnumerable<NoteSide> Candidates(NoteSide preferred, NoteSide? exclude) =>
        Cycle
            .SkipWhile(s => s != preferred)
            .Concat(Cycle.TakeWhile(s => s != preferred))
            .Where(s => s != exclude);

    private static Rect WindowFor(Point anchorDip, NoteSide side, Size cardDip)
    {
        double w = cardDip.Width, h = cardDip.Height;
        return side switch
        {
            // Card right of the anchor: tail strip between the window's left edge and the card.
            NoteSide.Right => new(
                anchorDip.X + Constants.TipGapDip,
                anchorDip.Y - h / 2,
                w + Constants.TailLengthDip,
                h),
            // Card left of the anchor: tail strip on the window's right.
            NoteSide.Left => new(
                anchorDip.X - Constants.TipGapDip - Constants.TailLengthDip - w,
                anchorDip.Y - h / 2,
                w + Constants.TailLengthDip,
                h),
            // Card below the anchor: tail strip between the window's top edge and the card.
            NoteSide.Below => new(
                anchorDip.X - w / 2,
                anchorDip.Y + Constants.TipGapDip,
                w,
                h + Constants.TailLengthDip),
            // Card above the anchor: tail strip at the window's bottom.
            _ => new(
                anchorDip.X - w / 2,
                anchorDip.Y - Constants.TipGapDip - Constants.TailLengthDip - h,
                w,
                h + Constants.TailLengthDip),
        };
    }

    private static NotePlacement Build(NoteSide side, Rect window, Size cardDip, Point anchorDip, bool clamped)
    {
        var card = CardFor(side, window, cardDip);
        return new NotePlacement(side, window, card, TailTipFor(side, card, window, anchorDip, clamped));
    }

    private static Rect CardFor(NoteSide side, Rect window, Size cardDip) => side switch
    {
        NoteSide.Right => new(window.Left + Constants.TailLengthDip, window.Top, cardDip.Width, cardDip.Height),
        NoteSide.Left => new(window.Left, window.Top, cardDip.Width, cardDip.Height),
        NoteSide.Below => new(window.Left, window.Top + Constants.TailLengthDip, cardDip.Width, cardDip.Height),
        _ => new(window.Left, window.Top, cardDip.Width, cardDip.Height),
    };

    /// <summary>The tail tip: <see cref="Constants.TipGapDip"/> off the anchor toward the card,
    /// with the along-edge coordinate clamped into the card (inset so the triangle's base stays
    /// inside it) — "the tail bends to the anchor". On the clamped fallback the anchor may sit
    /// under the card: the tail hides there (P3 D2.3), and otherwise points at the nearest
    /// window point, still kept TipGapDip clear of the card.</summary>
    private static Point? TailTipFor(NoteSide side, Rect card, Rect window, Point anchorDip, bool clamped)
    {
        if (clamped)
        {
            if (card.Contains(anchorDip))
                return null; // the anchor ended up under the card: no tail
            double nx = Clamp(anchorDip.X, window.Left + Constants.TipGapDip, Math.Max(window.Left + Constants.TipGapDip, window.Right - Constants.TipGapDip));
            double ny = Clamp(anchorDip.Y, window.Top + Constants.TipGapDip, Math.Max(window.Top + Constants.TipGapDip, window.Bottom - Constants.TipGapDip));
            return ClampTipAlongEdge(side, card, new Point(nx, ny));
        }

        return side switch
        {
            NoteSide.Right => ClampTipAlongEdge(side, card,
                new Point(anchorDip.X + Constants.TipGapDip, anchorDip.Y)),
            NoteSide.Left => ClampTipAlongEdge(side, card,
                new Point(anchorDip.X - Constants.TipGapDip, anchorDip.Y)),
            NoteSide.Below => ClampTipAlongEdge(side, card,
                new Point(anchorDip.X, anchorDip.Y + Constants.TipGapDip)),
            _ => ClampTipAlongEdge(side, card,
                new Point(anchorDip.X, anchorDip.Y - Constants.TipGapDip)),
        };
    }

    /// <summary>Clamps the tip's along-edge coordinate into the card inset by the base half-width
    /// plus a 4 DIP margin, so the triangle's base is always fully inside the card.</summary>
    private static Point ClampTipAlongEdge(NoteSide side, Rect card, Point tip)
    {
        double lo = Constants.TailHalfWidthDip + 4;
        return side switch
        {
            NoteSide.Right or NoteSide.Left => new Point(
                tip.X, Clamp(tip.Y, card.Top + lo, Math.Max(card.Top + lo, card.Bottom - lo))),
            _ => new Point(
                Clamp(tip.X, card.Left + lo, Math.Max(card.Left + lo, card.Right - lo)), tip.Y),
        };
    }

    private static bool FitsInside(Rect window, Rect inset) =>
        window.Left >= inset.Left && window.Top >= inset.Top &&
        window.Right <= inset.Right && window.Bottom <= inset.Bottom;

    private static bool IntersectsAny(Rect window, IReadOnlyList<Rect> avoid)
    {
        foreach (var r in avoid)
        {
            if (window.IntersectsWith(r))
                return true;
        }
        return false;
    }

    private static Rect Inset(Rect workArea, double margin) => new(
        workArea.Left + margin,
        workArea.Top + margin,
        Math.Max(0, workArea.Width - 2 * margin),
        Math.Max(0, workArea.Height - 2 * margin));

    private static Rect ClampInto(Rect window, Rect bounds) => new(
        Clamp(window.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - window.Width)),
        Clamp(window.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - window.Height)),
        window.Width,
        window.Height);

    private static double Clamp(double v, double lo, double hi) => Math.Clamp(v, lo, hi);

    /// <summary>The placement constants (P3 D2). DIP everywhere; the tail's tip stops
    /// TipGapDip short of the anchor; the edge margin is the control hit half-size plus 24 DIP
    /// (D2.4 — raise it if the hardware check shows off-screen gaze clamping to the edge).</summary>
    internal static class Constants
    {
        public const double TailLengthDip = 30.0;
        public const double TipGapDip = 6.0;
        public const double TailHalfWidthDip = 18.0;
        public const double EdgeMarginDip = (LayoutConstants.NoteCellDip / 2) + 24.0;

        /// <summary>The clearance an avoid rect keeps around a note (T6 fixup-06 G2): a
        /// displaced note stops this far outside the keyboard's edge, so a gaze on the key at
        /// that edge is never on a note control. A gaze target is a DISC of
        /// <see cref="LayoutConstants.NoteButtonRadius"/> (28 DIP) around its centre, and the
        /// half of that plus a hand's width of slack is the 24 DIP the caret needs to cross
        /// from the keyboard to the note without a dwell ever landing on both.</summary>
        public const double AvoidGapDip = 24.0;
    }
}
