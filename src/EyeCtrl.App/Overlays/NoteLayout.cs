namespace EyeCtrl.App.Overlays;

using System.Windows;
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
    /// card's RIGHT edge (the default, and the side away from the tail for Right/Below/Above).</summary>
    public static double ButtonColumnCenterX =>
        LayoutConstants.NoteColumnWidthDip - LayoutConstants.NoteButtonPadding - (LayoutConstants.NoteCellDip / 2);

    /// <summary>Window-relative X of the hit-cell column centre when the column sits on the card's
    /// LEFT edge - the side away from the tail on a <see cref="NoteSide.Left"/> placement.</summary>
    public static double ButtonColumnCenterXFromLeft =>
        LayoutConstants.NoteButtonPadding + (LayoutConstants.NoteCellDip / 2);

    /// <summary>Window-relative Y of hit-cell row <paramref name="row"/>'s centre (P4 D3): the
    /// rows are the four stacked 80 DIP cells between the column paddings.</summary>
    public static double RowCenterY(int row) =>
        LayoutConstants.NoteButtonPadding + (row * LayoutConstants.NoteCellDip) + (LayoutConstants.NoteCellDip / 2);

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
    /// </summary>
    public static (double X, double Y) ButtonCenter(NoteSide side, int slot)
    {
        double colX = side == NoteSide.Left ? ButtonColumnCenterXFromLeft : ButtonColumnCenterX;
        var (originX, originY) = CardOrigin(side);
        return (originX + colX, originY + RowCenterY(slot));
    }

    // == the card size (P4 D2): the text size drives the width, the measured text height
    // (capped) drives the height, and the control column is the floor. ==

    /// <summary>The text column width in DIP: round(15 x size), about 28-32 characters per line.
    /// 420 DIP at the provisional 28.</summary>
    public static double TextColumnWidthDip(double textSize) => Math.Round(15 * textSize);

    /// <summary>The card width in DIP: the text column plus the control column.</summary>
    public static double CardWidthDip(double textSize) =>
        TextColumnWidthDip(textSize) + LayoutConstants.NoteColumnWidthDip;

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

    /// <summary>The chip text column's width in DIP: the "Changes discarded - Undo" strings wrap
    /// to two lines in it at the body size.</summary>
    public const double ChipTextWidthDip = 140.0;

    /// <summary>The chip card size (P4 D3's state table): at least one cell wide (it is the
    /// cell plus the text column) and one cell row tall.</summary>
    public static Size ChipCardSize() => new(
        ChipTextWidthDip + LayoutConstants.NoteColumnWidthDip,
        (2 * LayoutConstants.NoteButtonPadding) + LayoutConstants.NoteCellDip);
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
    /// </summary>
    /// <param name="anchor">The anchor in physical pixels; converted to DIP once, here.</param>
    /// <param name="preferred">The side tried first.</param>
    /// <param name="cardDip">The card size in DIP (today's fixed card; T5 keeps this the input).</param>
    /// <param name="workAreaDip">The work area in DIP (injected; production: SystemParameters.WorkArea).</param>
    /// <param name="avoidDip">Rects the window must not intersect, in DIP (Edit: keyboard + edge bar).</param>
    /// <param name="scale">The DPI scale that converts the physical anchor into DIP.</param>
    /// <param name="exclude">A side never to choose, or null.</param>
    /// <returns>The placement, or null only when <paramref name="exclude"/> removed the only
    /// fitting side.</returns>
    public static NotePlacement? Place(
        PhysicalPoint anchor,
        NoteSide preferred,
        Size cardDip,
        Rect workAreaDip,
        IReadOnlyList<Rect> avoidDip,
        double scale,
        NoteSide? exclude = null)
    {
        var anchorDip = new Point(anchor.X / scale, anchor.Y / scale);
        var inset = Inset(workAreaDip, Constants.EdgeMarginDip);

        foreach (var side in Candidates(preferred, exclude))
        {
            var window = WindowFor(anchorDip, side, cardDip);
            if (!FitsInside(window, inset))
                continue;
            if (IntersectsAny(window, avoidDip))
                continue;
            return Build(side, window, cardDip, anchorDip, clamped: false);
        }

        if (exclude is not null)
            return null; // P3 D2: null only when exclude removed the only fitting side

        // No fit at all: clamp the preferred side's window inside the inset work area (P3 D2.3).
        var clamped = WindowFor(anchorDip, preferred, cardDip);
        clamped = ClampInto(clamped, inset);
        return Build(preferred, clamped, cardDip, anchorDip, clamped: true);
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
    }
}
