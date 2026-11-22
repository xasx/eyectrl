namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Models;
using EyeCtrl.App.Services.Notes;

public class NoteWindow : OverlayWindow
{
    private const double ButtonRadius = LayoutConstants.NoteButtonRadius;
    private const double ButtonDiameter = ButtonRadius * 2;
    private const double ButtonPadding = LayoutConstants.NoteButtonPadding;
    private const double CellDip = LayoutConstants.NoteCellDip;

    /// <summary>The hit-cell fill (P2 D2 layout rule 2): the token near-black at 1/255
    /// opacity - alpha &gt; 0 under the whole hit area, invisible against the card.</summary>
    private static readonly Brush CellFillBrush = CreateCellFillBrush();
    // Frozen: the window is constructed on whichever STA thread runs, and a frozen Freezable
    // is the one WPF shape that is shareable across them.
    private static Brush CreateCellFillBrush()
    {
        var brush = new SolidColorBrush(Tokens.StrokeHalo) { Opacity = 1.0 / 255.0 };
        brush.Freeze();
        return brush;
    }

    private readonly string _noteId;
    private readonly Canvas _canvas;
    private readonly Dictionary<string, Ellipse> _buttons = new();
    private readonly Dictionary<string, TextBlock> _buttonLabels = new();
    private readonly Dictionary<string, (Path Arc, double StartAngle)> _arcs = new();
    private readonly Dictionary<int, Rectangle> _cellFills = new();
    /// <summary>The current card width in DIP (T5 review-01 B1): the ONE source the drawn
    /// cells, the column and the text placement share, taken from the applied placement.</summary>
    private double CardWidthDip =>
        _placement is { } p ? NoteLayout.RectLocal(p).Width : NoteLayout.CardWidthDip(_textSize);

    private TextBlock? _textBlock;
    private TextBlock? _chipTextBlock;
    private Border? _contentBorder;
    // The Move hint (sitting 9): the "where should it go" line the dimmed card shows, drawn as a
    // footer in the card's text column. Tracked so a state change can take it down again.
    private Border? _moveHint;
    private TextBlock? _moveHintText;
    // The card's own drawn pieces (sitting 9): the Move state dims THESE — border/background,
    // body text, number tag and tail — to 0.6, while the hint plate and the Move discs stay at
    // full opacity. A window-level Opacity dimmed every child, which made the hint grey.
    private Border? _cardBorder;
    private Polygon? _tail;
    // Fixup-01 F3: the alpha-1 fill under the at-rest reveal target. It makes the enlarged
    // band painted and hit-testable, so WindowFromPoint can report this note there; hidden
    // while the row is revealed (the target is unregistered then).
    private Rectangle? _revealFill;
    private const double MoveDimOpacity = 0.6;
    private NoteState _state;
    // D (sitting 11): whether a Display note currently shows its revealed control row. The
    // manager owns the state and drives this; the at-rest card is the reserved rect's top part.
    private bool _revealed;
    // T6 fixup-01: the tag elements are tracked so DrawBadge can replace them without a full Draw.
    private Border? _badgeTag;
    private TextBlock? _badgeLabel;
    private int? _badgeNumber;
    // The note body text size in DIP (P4 D1): pushed by the manager at creation and on every
    // T8 raise; every text measurement and drawn font derives from it.
    private double _textSize = NoteSettings.DefaultTextSizeDip;

    private string _chipText = string.Empty;
    // The chip's own text size and text-column width (T6 fixup-06 G1): the chip reads at the
    // NOTE's body size, and the manager owns the measured column both of them share. They are
    // pushed, not derived here, so the drawn text and the card the manager placed can never
    // disagree about the size the chip was composed at.
    private double _chipTextSize = NoteSettings.DefaultTextSizeDip;
    private double _chipColumnWidth = 0;
    // The note's body text (fixup-01 B1): UpdateContent rebuilds the text block plainly empty,
    // and the re-place paths (T6, the probe raise, Move's tails) never re-push SetText — the
    // window alone keeps the last body text across a redraw.
    private string _text = string.Empty;
    // The side the placement chose (P3 D2); Right until the manager applies a placement.
    private NoteSide _side = NoteSide.Right;
    // The placement the manager last applied (P3 D2): every drawn rect — the card, the title
    // bar, the tail, the column — derives from it. Null only before the first ApplyPlacement.
    private NotePlacement? _placement;

    public string NoteId => _noteId;
    public NoteState State => _state;

    public NoteWindow(string noteId)
    {
        _noteId = noteId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsClickThrough = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The pre-placement size is a placeholder: every real size arrives through
        // ApplyPlacement before Show. It is the DEFAULT-size card, so a window measured before
        // its first placement still reports sane bounds.
        Width = NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip);
        Height = NoteLayout.ControlsColumnHeightDip;

        _canvas = new Canvas { ClipToBounds = false };
        Content = _canvas;
        AttachStalePressGuards();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Applies a derived placement (P3 D2): resizes the window to the placement's window rect
    /// (card + tail zone) and redraws everything — the column follows the side, the tail appears
    /// beside the card. The manager sets <c>Left</c>/<c>Top</c> from the same placement, so the
    /// drawn geometry and the window position can never disagree. Safe before Show(): Draw is
    /// idempotent and the Loaded handler re-runs it. Internal: <see cref="NotePlacement"/> is,
    /// and only the manager (same assembly) applies placements.
    /// <para>
    /// T6 fixup-01: the window rides <see cref="NoteLayout.WithTagClearance"/> on entry — every
    /// applied placement carries the tag's top strip (idempotent), so a window applied from any
    /// seam grows the same way and the tag never draws outside the window bounds. Fixup-04: the
    /// strip follows THIS window's text size, the same size the manager grew the placement with
    /// (it sets <see cref="SetTextSize"/> before every placement, and T8 re-sizes the strip with
    /// the re-derivation).
    /// </para>
    /// </summary>
    internal void ApplyPlacement(NotePlacement placement)
    {
        placement = NoteLayout.WithTagClearance(placement, _textSize);
        _placement = placement;
        _side = placement.Side;
        Dispatcher.Invoke(() =>
        {
            Width = placement.Window.Width;
            Height = placement.Window.Height;
            Draw();
        });
    }

    /// <summary>Test accessor (P4 D1/T8): the text size the window currently renders at.</summary>
    internal double TextSizeForTest => _textSize;

    /// <summary>Test accessor (D): whether the window currently draws the revealed control row.</summary>
    internal bool RevealedForTest => _revealed;

    /// <summary>
    /// D (sitting 11): shows or hides the Display control row. The manager calls this exactly on
    /// a reveal and on a hide; the at-rest card shrinks to the reserved rect's top part and the
    /// text does not move (its origin is the card's top-left in both states). Rebuilds the drawn
    /// tree because the card body, the row's cell fills and the buttons all change.
    /// </summary>
    internal void SetRevealed(bool revealed)
    {
        if (_state != NoteState.Display)
        {
            _revealed = false; // a reveal exists only on the Display card
            return;
        }
        if (_revealed == revealed) return;
        _revealed = revealed;
        Dispatcher.Invoke(Draw);
    }

    /// <summary>
    /// The at-rest Display card's size (D1), measured from the drawn body text and the applied
    /// placement: the reserved card height fixes the cap the manager derived it with (card height
    /// minus the row and the paddings), so the window's at-rest rect and the manager's reveal
    /// target read the same answer. The width is the MEASURED wrapped text plus paddings.
    /// </summary>
    internal Size RestCardSizeDip()
    {
        if (_placement is not { } placement)
            return new Size(
                NoteLayout.DisplayRestWidthDip(0),
                NoteLayout.DisplayRestHeightDip(0));
        double cap = Math.Max(0, placement.Card.Height
            - (2 * LayoutConstants.NoteButtonPadding) - LayoutConstants.NoteCellDip);
        double textHeight = Math.Min(MeasureTextHeightDip(_text), cap);
        return NoteLayout.DisplayRestSize(MeasureTextWidthDip(_text), textHeight);
    }

    /// <summary>The drawn body card rect in WINDOW-relative DIP (D): the reserved (grown Move)
    /// card while revealed or moving, the at-rest top part otherwise.</summary>
    private Rect DrawnCardLocal()
    {
        var card = _placement is { } p ? NoteLayout.RectLocal(p)
            : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
        bool expanded = _state is NoteState.Move or NoteState.Edit or NoteState.PendingUndo
            || _revealed;
        if (expanded) return card;
        var rest = RestCardSizeDip();
        return new Rect(card.Left, card.Top, rest.Width, rest.Height);
    }

    /// <summary>
    /// The body text's area height in DIP (D): the room the manager budgeted for the text, so
    /// the over-cap fade/ellipsis and the drawn block agree. At rest it is the at-rest card minus
    /// its paddings; revealed it is the reserved card minus the row and paddings; Move subtracts
    /// its hint block too (the grown card exists for the hint, not for more text).
    /// </summary>
    private double BodyTextAreaHeightDip()
    {
        if (_placement is not { } placement)
            return Math.Max(1, NoteLayout.ControlsColumnHeightDip - (2 * ButtonPadding));
        var drawn = DrawnCardLocal();
        if (_state == NoteState.Edit)
            return NoteLayout.EditTextAreaHeightDip(drawn.Height, _textSize);
        double subtract = _state == NoteState.Move
            ? LayoutConstants.NoteCellDip + MoveHintBlockHeightDip(placement.Card.Width)
            : _revealed ? LayoutConstants.NoteCellDip : 0;
        return Math.Max(1, drawn.Height - (2 * ButtonPadding) - subtract);
    }

    /// <summary>
    /// The wrapped body text's WIDTH in DIP (D1): measured with the real drawn face at the text
    /// column, so a short note's card is short and a full line's card is exactly as wide as the
    /// line. The manager reads it on every placement re-derivation and the render pass reads it
    /// again.
    /// </summary>
    internal double MeasureTextWidthDip(string text) => MeasureTextWidthDip(text, _textSize);

    internal double MeasureTextWidthDip(string text, double textSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double column = NoteLayout.TextColumnWidthDip(textSize);
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            NoteLayout.SemiBoldFace, textSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0)
        { MaxTextWidth = column };
        return Math.Min(column, formatted.Width);
    }

    /// <summary>The Move hint's plate height in DIP for a card width (D6, option C): the hint
    /// text's measured height at the plate's text column plus its two margins. The manager grows
    /// the drawn Move card by exactly this, and the window draws the plate with it.</summary>
    internal double MoveHintBlockHeightDip(double cardWidthDip)
    {
        double plate = NoteLayout.MoveHintWidthDip(cardWidthDip, _textSize, NoteLayout.MoveCardInsetDip);
        return MeasureMoveHintTextHeightDip(NoteLayout.MoveHintTextWidthDip(plate))
            + (2 * NoteLayout.MoveHintMarginDip);
    }

    /// <summary>The Move hint text's wrapped height at a text width, with the face it draws.</summary>
    internal double MeasureMoveHintTextHeightDip(double textWidth)
    {
        var block = new TextBlock
        {
            Width = Math.Max(1, textWidth),
            FontSize = NoteLayout.MoveHintFontDip(_textSize),
            FontFamily = Tokens.FontStandard,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = NoteLayout.TextLineHeightDip(NoteLayout.MoveHintFontDip(_textSize)),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = StringTable.Current.T("overlays.note.moveHint")
        };
        block.Measure(new Size(Math.Max(1, textWidth), double.PositiveInfinity));
        return block.DesiredSize.Height;
    }

    // T6 fixup-08 R4: set when the manager's show seam skipped the HWND show.
    private bool _displayedWithoutHwnd;

    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false -
    /// performs what <see cref="Window.Show"/> builds for the rest of the app (the drawn tree,
    /// which the Loaded handler would have built) without ever putting a window on the screen
    /// (T6 fixup-08 R4). The headless tests run on the OWNER's machine, where a topmost note
    /// window is exactly what must not appear; the placement, the bounds, the target
    /// registrations and <see cref="IsDisplayed"/> behave as if the window had been shown.
    /// </summary>
    public void ShowForDisplay(bool realHwnd)
    {
        if (realHwnd)
        {
            Show();
            return;
        }

        _displayedWithoutHwnd = true;
        Draw(); // the Loaded handler's half: a shown window draws itself
    }

    /// <summary>Whether the window counts as on screen: the real HWND's IsVisible in production,
    /// or the seam's own flag when the window was never handed to the OS (R4).</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;
    /// <summary>Test accessor (fixup-01 G3): the drawn buttons, keyed per note id and set slot,
    /// so the window-geometry pin can check the canvas against NoteLayout.ButtonCenter.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, Ellipse> ButtonsForTest() => _buttons;

    internal System.Collections.Generic.IReadOnlyDictionary<string, TextBlock> LabelsForTest() => _buttonLabels;
    internal double ButtonRadiusForTest => ButtonRadius;

    /// <summary>Test accessor (D): the drawn tail polygon, or null when no tail is drawn.</summary>
    internal Polygon? TailForTest => _tail;

    /// <summary>Test accessor (notes readability, sitting 9): the drawn body text block — the
    /// element whose weight the owner reads, pinned SemiBold against drawn/measured drift.</summary>
    internal TextBlock? BodyTextForTest => _textBlock;

    /// <summary>Test accessor (T6 fixup-06 G1): the chip's drawn text block - its font size and
    /// its column are what the owner could not read, so they are read, not assumed.</summary>
    internal TextBlock? ChipTextForTest => _chipTextBlock;

    /// <summary>Test accessor (T6 fixup-06 G1): the text size the chip was composed at - the
    /// captured one, which a later text-size change does not move.</summary>
    internal double ChipTextSizeForTest => _chipTextSize;

    /// <summary>Test accessor (T6 fixup-02): the drawn cell fill of a hit-cell row, the element
    /// the caretaker input is wired to beside the disc and label — or null when the row never
    /// got one.</summary>
    internal Rectangle? CellFillForTest(int row) => _cellFills.TryGetValue(row, out var fill) ? fill : null;

    /// <summary>Test accessor (fixup-01 F3): the alpha-1 fill under the at-rest reveal target,
    /// or null while the row is revealed / the note is not at rest.</summary>
    internal Rectangle? RevealFillForTest => _revealFill;

    /// <summary>Test accessor (T6 fixup-02): the drawn dwell arc of a control, for the
    /// non-interactivity pins.</summary>
    internal Path? ArcForTest(string suffix) =>
        _arcs.TryGetValue($"{_noteId}:{suffix}", out var arc) ? arc.Arc : null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        // A redraw replaces every wired element: a pending press recorded against elements
        // this Draw just destroyed must not survive it.
        _pendingCaretakerPress = null;
        _buttons.Clear();
        _buttonLabels.Clear();
        _arcs.Clear();
        _cellFills.Clear();
        _moveHint = null;
        _moveHintText = null;
        _cardBorder = null;
        _tail = null;
        _revealFill = null;

        // Fixup-01 F3: at rest, paint the whole reveal target with the same alpha-1 fill the hit
        // cells carry, FIRST so it sits under the card/tail. The window was grown to contain the
        // target (NoteLayout.WithRevealTargetClearance), so those pixels now belong to this
        // window and WindowFromPoint can report it. The fill is hit-testable by construction
        // (non-null, alpha > 0 fill); revealing hides it because the target is unregistered then.
        if (_state == NoteState.Display && !_revealed && _placement is { } revealPlacement)
        {
            var rest = RestCardSizeDip();
            var restCard = NoteLayout.RestCardRect(revealPlacement, rest);
            var target = NoteLayout.RevealTargetRect(restCard);
            // The same one-DIP slack the window grew by (NoteLayout.WithRevealTargetClearance):
            // WPF snaps the HWND to whole device pixels, so a fill exactly on the target's edge
            // could leave a sub-pixel strip of the registered target unpainted.
            target.Inflate(1, 1);
            var revealFill = new Rectangle
            {
                Width = target.Width,
                Height = target.Height,
                Fill = CellFillBrush
            };
            Canvas.SetLeft(revealFill, target.Left - revealPlacement.Window.Left);
            Canvas.SetTop(revealFill, target.Top - revealPlacement.Window.Top);
            _canvas.Children.Add(revealFill);
            _revealFill = revealFill;
        }

        // P3 D2: the card sits inside the window at the placement card origin (the tail zone
        // takes the strip on the anchor side); the pointer tail is drawn INSIDE the window.
        // P4 D2: no title bar. D (sitting 11): the visible body is the at-rest top part of the
        // reserved card until a look reveals the row; Move/Edit/the chip draw their whole card.
        var cardLocal = _placement is { } p ? NoteLayout.RectLocal(p)
            : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
        var drawnLocal = DrawnCardLocal();
        bool rowLayout = (_state == NoteState.Display && _revealed) || _state == NoteState.Move;
        var borderBrush = GetBorderBrush();

        if (_placement?.TailVisible == true)
        {
            // The tail is NOT an offer zone (P2 D7): it is drawn under the card z-order and
            // ChromeRects never reports it. Its stroke is BorderSubtle in EVERY state (T5b):
            // the tail shows the caretaker what the note points at, so it owes the same WCAG
            // 1.4.11 3:1 the card edge owes, against a page of either lightness — and no state
            // brush gives that (SurfaceMedium sits at 1.1-1.5:1, the Accent/MarkerMove marks
            // carry state meaning the tail must not repeat). Constant on purpose, not
            // borderBrush: every Draw recreates the tail with this stroke while the card border
            // follows the state, so a state-varying stroke here would be wrong on every flip.
            // D: the triangle's BASE is clamped into the currently drawn card, so at rest it
            // stays attached to the short body instead of floating on the reserved edge; the
            // tip (the anchor relation) is untouched.
            var tail = new Polygon
            {
                Points = new PointCollection(NoteLayout.TailTriangle(_placement, drawnLocal)!),
                Fill = Tokens.BrushSurfaceDark,
                Stroke = Tokens.BrushBorderSubtle,
                StrokeThickness = 1
            };
            _canvas.Children.Add(tail);
            _tail = tail;
        }

        var bg = new Border
        {
            Width = drawnLocal.Width, Height = drawnLocal.Height,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = borderBrush,
            BorderThickness = GetBorderThickness(),
            // StrokeHalo is the palette near-black; at 0.4 opacity it is indistinguishable
            // from pure black, and it keeps the shadow on the token so a light theme can move it.
            Effect = new DropShadowEffect { Color = Tokens.StrokeHalo, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, drawnLocal.Left); Canvas.SetTop(bg, drawnLocal.Top);
        _canvas.Children.Add(bg);
        _cardBorder = bg;

        // P4 D3: every hit-cell row gets its alpha-1 fill (P2 D2 layout rule 2 - the hit area
        // sits on alpha-1/255 pixels even where its disc does not reach). K2a: Edit draws its
        // THREE cells in its centred row under the text. D: Display (revealed) and Move draw the
        // four-cell row under the text; the chip keeps the column. Drawn once per state for ALL
        // its rows, like the buttons; UpdateButtonVisibility picks the state's occupied cells.
        int cellRows = _state == NoteState.Edit ? 3 : 4;
        for (int row = 0; row < cellRows; row++)
        {
            var (cx, cy) = _placement is { } wp
                ? (rowLayout
                    ? NoteLayout.RowCellCenter(wp, row)
                    : _state == NoteState.Edit
                        ? NoteLayout.EditCellCenter(wp, row)
                        : _state == NoteState.PendingUndo
                            ? NoteLayout.ChipCellCenter(wp, row)
                            : NoteLayout.ButtonCenter(wp, row))
                : NoteLayout.ButtonCenter(_side, row, CardWidthDip);
            var fill = new Rectangle
            {
                Width = CellDip, Height = CellDip,
                Fill = CellFillBrush
            };
            Canvas.SetLeft(fill, cx - (CellDip / 2));
            Canvas.SetTop(fill, cy - (CellDip / 2));
            _canvas.Children.Add(fill);
            _cellFills[row] = fill;
        }

        _contentBorder = new Border
        {
            Width = drawnLocal.Width, Height = drawnLocal.Height,
            Background = Brushes.Transparent,
            // D: the text plate now sits OVER the row's cells (the row is under the text), so a
            // transparent-but-hit-testable border would swallow every caretaker touch on a cell
            // corner. The plate is layout only; the discs, glyphs and cell fills take the input.
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_contentBorder, drawnLocal.Left); Canvas.SetTop(_contentBorder, drawnLocal.Top);
        _canvas.Children.Add(_contentBorder);

        DrawButtons();
        UpdateContent();
        UpdateButtonVisibility();
        DrawBadge();
        UpdateMoveDim();
    }

    /// <summary>
    /// T6 P5: the manager pushes this note's badge numeral — null hides. Called on every
    /// membership or state change through the manager's <c>RefreshBadges</c>; every redraw path
    /// re-derives the drawn badge through <see cref="DrawBadge"/>.
    /// </summary>
    public void SetBadge(int? number)
    {
        if (_badgeNumber == number) return;
        _badgeNumber = number;
        Dispatcher.Invoke(DrawBadge);
    }

    /// <summary>
    /// Draws (or clears) the number tag — <see cref="NoteLayout.TagRect"/>: a small FILLED
    /// rounded rectangle straddling the card's top edge at the text-side end, out of the button
    /// column (T6 fixup-01, owner decision: the gap-row badge read as a fourth dwell button —
    /// a decoy next to ✕). The tag is pure chrome: never registered as a dwell target, never
    /// chrome of its own, carries no hit affordance. A PendingUndo chip NEVER draws one (T6:
    /// chips show no badge and don't count), guarded here as well as in the manager's push.
    /// </summary>
    private void DrawBadge()
    {
        if (_badgeTag != null) { _canvas.Children.Remove(_badgeTag); _badgeTag = null; }
        if (_badgeLabel != null) { _canvas.Children.Remove(_badgeLabel); _badgeLabel = null; }
        if (_badgeNumber is not { } number || _state == NoteState.PendingUndo || _placement is not { } placement)
        {
            UpdateMoveDim();
            return;
        }

        // Position: straddling the card's TOP edge at the TEXT-side end (the column hugs the
        // edge away from the tail), inset from the corner; on a Below placement the tail shares
        // the top edge, so the tag slides toward the column to clear the polygon. The window
        // grew upward by the protrusion when the badge pushed (WithTagClearance), so the top
        // half has room; the card's screen rect never moved.
        var rect = NoteLayout.TagRect(placement, _textSize);

        // Fill TextSecondary, numeral SurfaceDark, and the rim SurfaceDark at 1 DIP (T6
        // fixup-01 + owner addendum: a solid tag, no ring — the numeral's brush keys the body
        // so it reads as one inked piece, not a control). Contrast is gated in max(fill, edge)
        // form on the drawn brushes: the TEXT floor (numeral vs fill ≥ 4.5) holds everywhere;
        // the fill clears the card it straddles (worst case 6.57:1, floor 3.0); the pair clears
        // the pages of both lightnesses the protruding half can sit over (in Midnight/Ember/
        // Daylight the fill fails only the too-bright page and the dark rim carries it, in
        // Contrast the light fill and the black rim face each other's extremes) —
        // NoteBadgeContrastTests gates the drawn brushes.
        var tag = new Border
        {
            Width = rect.Width, Height = rect.Height,
            Background = Tokens.BrushTextSecondary,
            BorderBrush = Tokens.BrushSurfaceDark,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.SmallCornerRadius,
            // T6 fixup-02 B1: pure chrome, and a filled Border would otherwise be a
            // hit-testable surface over the card's top edge — a click on the tag must fall
            // through to whatever is under it, never stop here.
            IsHitTestVisible = false
        };
        Canvas.SetLeft(tag, rect.Left);
        Canvas.SetTop(tag, rect.Top);
        _canvas.Children.Add(tag);
        _badgeTag = tag;

        // The numeral centres on its INK (T5 fixup-03 G1's convention): digits like "1" carry
        // asymmetric advance boxes, and box centring reads off-centre. The font is 1.0× the
        // body text (notes readability, sitting 9: the owner chose the tag's SIZE as the second
        // lever; was 0.75× since T6 fixup-04) at SemiBold — the ink measurement below uses the
        // same face and weight, so the centring math and the drawn glyph cannot disagree.
        var label = new TextBlock
        {
            Text = number.ToString(CultureInfo.InvariantCulture),
            FontFamily = Tokens.FontStandard,
            FontSize = NoteLayout.TagNumeralFontDip(_textSize),
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.BrushSurfaceDark,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var formatted = new FormattedText(label.Text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, NoteLayout.SemiBoldFace, label.FontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        double cx = rect.Left + (rect.Width / 2);
        double cy = rect.Top + (rect.Height / 2);
        Canvas.SetLeft(label, cx - (ink.Left + (ink.Width / 2)));
        Canvas.SetTop(label, cy - (ink.Top + (ink.Height / 2)));
        _canvas.Children.Add(label);
        _badgeLabel = label;
        UpdateMoveDim();
    }


    /// <summary>
    /// P1 D3: the editor is NOT a TextBox. Edit shows the same display TextBlock followed by a
    /// static end marker (a thin accent bar) — the caret is always at the end in v1, so a blinking
    /// caret would promise a text navigation that does not exist. The note window is
    /// WS_EX_NOACTIVATE for life (the base class sets it once in OnSourceInitialized): typing
    /// reaches the note through the manager's <c>Apply</c>, never through OS focus, and a bare
    /// <c>Activate()</c> — whose foreground-lock failure sent eye-typed text into other windows —
    /// no longer exists on any path. PendingUndo (P2 D5) instead renders the Undo chip: the chip
    /// text the manager set through <see cref="SetChipText"/>, no editor, no end marker.
    /// </summary>
    public void UpdateContent()
    {
        if (_contentBorder == null) return;
        _contentBorder.Child = null;
        _textBlock = null;
        _chipTextBlock = null;
        RemoveMoveHint();
        OverCapForTest = false;
        OverCapFadeForTest = false;

        if (_state == NoteState.PendingUndo)
        {
            // The chip (P4 D3, styling final; T6 fixup-06 G1): the Undo cell plus the chip text
            // at the NOTE's own text size - 12 DIP was unreadable on the owner's screen - in the
            // shrunk-in-place window. The column width is the manager's MEASURED one (the
            // narrowest that holds every chip text in two lines), so the text never leaves it.
            _chipTextBlock = new TextBlock
            {
                Width = _chipColumnWidth,
                MaxWidth = _chipColumnWidth,
                // Fixup-01 F2: the Undo column is now on the LEFT, so the column itself is the
                // text's left gap; a further 10 DIP margin would push the measured column 10 DIP
                // past the card (the column's width is exactly cell + 2 x padding). The top
                // margin stays.
                Margin = new Thickness(0, NoteLayout.ChipTextMarginDip, 0, 0),
                FontSize = NoteLayout.ChipTextFontDip(_chipTextSize),
                FontFamily = Tokens.FontStandard,
                // Notes readability (sitting 9): the chip reads SemiBold like the body - stroke
                // weight was the lever the owner chose, and NoteLayout.MeasureChip measures the
                // column at the same weight, so drawn and measured can never drift.
                FontWeight = FontWeights.SemiBold,
                Foreground = Tokens.BrushTextPrimary,
                TextWrapping = TextWrapping.Wrap,
                // The body's 1.25x leading: the card's height is derived from the lines this
                // produces, so the two must agree.
                LineHeight = NoteLayout.TextLineHeightDip(_chipTextSize),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Text = _chipText
            };
            _contentBorder.Child = _chipTextBlock;
            // B1: beside the column here too.
            var chipCard = _placement is { } cp ? NoteLayout.RectLocal(cp)
                : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
            _contentBorder.Width = _chipColumnWidth;
            // Fixup-01 F2: the chip's Undo column is on the LEFT in every side (the chip has no
            // tail), so the text always starts right of it — at the cell's right edge (the two
            // paddings plus one cell, 122 DIP from the card's left). Fixup-02 F2: it used to start
            // at the whole column width (132 DIP), which left the measured column's longest line —
            // the "discarded" chip in EN and DE — ending 1.1 to 1.8 DIP INSIDE the 2 DIP border.
            // The 10 DIP between the cell edge and the card's right padding is the text's right
            // inset; the CARD size does not change, and ↺ stays where it is (336 DIP from ✕).
            Canvas.SetLeft(_contentBorder,
                chipCard.Left + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteCellDip);
            Canvas.SetTop(_contentBorder, chipCard.Top);
            return;
        }

        // P4 D2: one primary-colour body for Display and Edit alike (the amber Display accent is
        // gone with the title bar). Over the cap, Display clips from the TOP with a fade and an
        // ellipsis; Edit clips to the TAIL of the draft, where the end marker sits.
        double avail = BodyTextAreaHeightDip();
        bool overCap = MeasureTextHeightDip(_text) > avail + 0.5;
        string shown = overCap
            ? _state == NoteState.Edit
                ? TailThatFits(_text, avail)
                : HeadThatFits(_text, Math.Max(1, avail - NoteLayout.TextLineHeightDip(_textSize))) + "…"
            : _text;

        _textBlock = new TextBlock
        {
            // Width is the text column, capped by the DRAWN card's inner width (D): the reserved
            // width is at least the measured text, so the wrap it was measured with holds and
            // the block's box never spills past the card.
            Width = Math.Min(NoteLayout.TextColumnWidthDip(_textSize),
                Math.Max(1, DrawnCardLocal().Width - (2 * ButtonPadding))),
            MaxWidth = NoteLayout.TextColumnWidthDip(_textSize),
            Margin = new Thickness(10, 10, 0, 0),
            FontSize = _textSize,
            FontFamily = Tokens.FontStandard,
            // Notes readability (sitting 9): SemiBold in EVERY state - Display and Edit draw
            // the same block (P4 D2), so one weight covers both. MeasureTextHeightDip measures
            // at the same weight, so the fit/wrap math and the card height derivation (the
            // manager reads this same measure) cannot drift from what is drawn.
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
            // The P4 D2 leading: 1.25x, pinned as a block height so every line is identical.
            LineHeight = NoteLayout.TextLineHeightDip(_textSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = shown
        };

        if (overCap && _state != NoteState.Edit)
        {
            // The P4 D2 fade, DISPLAY ONLY, only over the cap.
            _textBlock.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Tokens.StrokeHalo, 0.0),
                    new GradientStop(Tokens.StrokeHalo, 0.72),
                    new GradientStop(Colors.Transparent, 1.0),
                }
            };
            OverCapFadeForTest = true;
        }
        OverCapForTest = overCap;

        var stack = new StackPanel();
        stack.Children.Add(_textBlock);
        if (_state == NoteState.Edit)
        {
            // The static end marker (P1 D3): where a caret would blink in a real editor, a thin
            // bar states plainly that typed text lands after everything shown. K2a: its height
            // and gap come from NoteLayout, which also budgets them in the compact card's height,
            // so the marker can never draw into the control row below.
            var endMarker = new Rectangle
            {
                Width = NoteLayout.EditEndMarkerHeightDip,
                Height = NoteLayout.EditEndMarkerHeightDip,
                Fill = Tokens.BrushAccent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, NoteLayout.EditEndMarkerGapDip, 0, 0)
            };
            stack.Children.Add(endMarker);
        }

        _contentBorder.Child = stack;

        // T5 review-01 B1 was the column; D (sitting 11) has no column on any card: the text
        // always starts at the card's top-left corner (the text corner) in every state, so the
        // text origin is identical at rest and revealed. Move/Edit/the chip draw their full card;
        // at rest the top-left is the reserved rect's top-left too.
        var card = _placement is { } p ? NoteLayout.RectLocal(p)
            : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
        _contentBorder.Width = NoteLayout.TextColumnWidthDip(_textSize) + ButtonPadding;
        Canvas.SetLeft(_contentBorder, card.Left);
        Canvas.SetTop(_contentBorder, card.Top);

        if (_state == NoteState.Move && _placement is { } placement)
            DrawMoveHint(placement);
    }

    /// <summary>Test accessors (sitting 9): the Move hint's drawn box and text, or nulls when the
    /// card is not in Move. Read, not assumed — the "gone after place/Cancel" pins read these.</summary>
    internal Border? MoveHintForTest => _moveHint;
    internal TextBlock? MoveHintTextForTest => _moveHintText;

    /// <summary>Test accessors (sitting 9 F1): the card's own drawn pieces, whose opacity is the
    /// Move dim — and the window's own Opacity, which is always 1.0 so it cannot multiply the
    /// hint grey.</summary>
    internal Border? CardBorderForTest => _cardBorder;
    internal Border? ContentBorderForTest => _contentBorder;

    /// <summary>
    /// Draws the Move hint (sitting 9, row c; D6 option C): one message at the note's text size
    /// and SemiBold — the German instruction "Auf den neuen Platz für die Notiz schauen" — as a
    /// plate directly UNDER the dimmed body text and ABOVE the ⇄/⊘ row. The drawn Move card has
    /// already grown downward by this plate's height (the manager grows it), so the instruction
    /// is never clipped and never smaller; the row keeps its own band at the card's bottom. It is
    /// NOT part of the Move dim (F1): the owner must be able to read it.
    /// </summary>
    private void DrawMoveHint(NotePlacement placement)
    {
        string text = StringTable.Current.T("overlays.note.moveHint");
        double font = NoteLayout.MoveHintFontDip(_textSize);
        double inset = GetBorderThickness().Left;
        double plateWidth = NoteLayout.MoveHintWidthDip(placement.Card.Width, _textSize, inset);
        double column = NoteLayout.MoveHintTextWidthDip(plateWidth);

        _moveHintText = new TextBlock
        {
            Width = column,
            FontSize = font,
            FontFamily = Tokens.FontStandard,
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
            // The body's own leading, so the hint reads as part of the card's typography; the
            // box height is measured from this very block, so the two cannot disagree.
            LineHeight = NoteLayout.TextLineHeightDip(font),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = text,
            IsHitTestVisible = false
        };
        _moveHintText.Measure(new Size(column, double.PositiveInfinity));
        double textHeight = _moveHintText.DesiredSize.Height;

        var rect = NoteLayout.MoveHintRect(placement, _textSize, textHeight, inset);
        _moveHint = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            Background = Tokens.BrushSurfaceDark,
            Padding = new Thickness(NoteLayout.MoveHintMarginDip, 0, NoteLayout.MoveHintMarginDip, 0),
            Child = _moveHintText,
            IsHitTestVisible = false,
            // The measured text fits by construction; the clip is the guarantee that a rounding
            // difference can never paint a line over the row.
            ClipToBounds = true
        };
        Canvas.SetLeft(_moveHint, rect.Left);
        Canvas.SetTop(_moveHint, rect.Top);
        _canvas.Children.Add(_moveHint);
    }

    /// <summary>Takes the Move hint down (every non-Move redraw).</summary>
    private void RemoveMoveHint()
    {
        if (_moveHint is not null)
            _canvas.Children.Remove(_moveHint);
        _moveHint = null;
        _moveHintText = null;
    }

    /// <summary>Test seams (P4 acceptance): whether the current render clips over the cap, and
    /// whether the fade+ellipsis treatment is applied (Display only).</summary>
    internal bool OverCapForTest { get; private set; }

    internal bool OverCapFadeForTest { get; private set; }

    /// <summary>Measures the wrapped text height in DIP the auto-height derives from (P4 D2):
    /// the same style the card body draws, measured at the text column width.</summary>
    internal double MeasureTextHeightDip(string text) => MeasureTextHeightDip(text, _textSize);

    /// <summary>Measures the wrapped text height in DIP at an EXPLICIT text size (T6 fixup-06
    /// G1): the chip's captured size, which the window's own current size may no longer be —
    /// a pending chip keeps the size it was composed at, so the full card it is measured
    /// against has to be measured at that size too. Measured at the DRAWN weight (notes
    /// readability, sitting 9): the body draws SemiBold, so the fit/wrap math and the card
    /// heights the manager derives from this measure measure SemiBold too.</summary>
    internal double MeasureTextHeightDip(string text, double textSize)
    {
        double width = NoteLayout.TextColumnWidthDip(textSize);
        var block = new TextBlock
        {
            FontSize = textSize,
            FontFamily = Tokens.FontStandard,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = NoteLayout.TextLineHeightDip(textSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = text,
            Width = width
        };
        block.Measure(new Size(width, double.PositiveInfinity));
        return block.DesiredSize.Height;
    }

    /// <summary>The longest prefix that fits <paramref name="avail"/> DIP (binary search over
    /// character counts; the caller appends the ellipsis).</summary>
    private string HeadThatFits(string text, double avail)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureTextHeightDip(text[..mid]) <= avail) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo];
    }

    /// <summary>The longest suffix that fits <paramref name="avail"/> DIP - Edit over the cap
    /// shows the end of the draft, where the end marker is (P4 D2).</summary>
    private string TailThatFits(string text, double avail)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureTextHeightDip(text[^mid..]) <= avail) lo = mid;
            else hi = mid - 1;
        }
        return text[^lo..];
    }

    /// <summary>Pushes a new body text (P1 typing, re-places, restores). The clip point moves
    /// with the text, so a live block re-renders. At rest the CARD SIZE is the measured text
    /// (D1), so a Display text change rebuilds the drawn tree; in Edit a keystroke only re-renders
    /// the content (the Edit card's size is the manager's placement).</summary>
    public void SetText(string text)
    {
        _text = text;
        if (_textBlock == null)
            return;
        Dispatcher.Invoke(_state == NoteState.Display ? Draw : UpdateContent);
    }

    /// <summary>Pushes the text size (P4 D1/T8). Every measurement and the drawn font follow.</summary>
    public void SetTextSize(double textSize)
    {
        _textSize = textSize;
        if (_contentBorder != null)
            Dispatcher.Invoke(() =>
            {
                UpdateContent();
                // T8: the tag is size-DEPENDENT now (fixup-04: the numeral scales with the
                // body size — 1.0× since notes readability, sitting 9 — and the derived box
                // around it), and DrawBadge redraws it from the
                // current placement — ApplyPlacementToWindow skips identical rects, so Draw
                // would not run and a placement-inflight read must not go stale. The window
                // rect itself follows with the manager's re-derivation from the T8 trigger.
                DrawBadge();
            });
    }


    public string GetText() => _textBlock?.Text ?? "";

    /// <summary>Sets the Undo chip text (P2 D5) and re-renders when the chip is showing. The
    /// text comes from the manager through the localization table, kind-dependent.</summary>
    /// <param name="text">The chip's line.</param>
    /// <param name="textSizeDip">The chip's font size - the note's body text size (T6 fixup-06
    /// G1). The manager CAPTURES it with the chip, so a later text-size change does not resize
    /// a chip the owner is already reaching for.</param>
    /// <param name="columnWidthDip">The measured text column the chip's text is drawn in.</param>
    public void SetChipText(string text, double textSizeDip, double columnWidthDip)
    {
        _chipText = text;
        _chipTextSize = textSizeDip;
        _chipColumnWidth = columnWidthDip;
        if (_state == NoteState.PendingUndo)
            Dispatcher.Invoke(UpdateContent);
    }

    /// <summary>
    /// Flips the presentation state. D: the drawn CARD BODY changes size between the at-rest
    /// Display card, the revealed/Move card and the Edit/chip card, so every flip rebuilds the
    /// whole tree from the applied placement — the manager applies the new placement around this
    /// call, and the last draw of the UI turn is the visible one (no flash). A reveal exists only
    /// in Display; every other state is at rest until shown otherwise.
    /// </summary>
    public void SetState(NoteState state)
    {
        if (state != NoteState.Display)
            _revealed = false;
        _state = state;
        Dispatcher.Invoke(Draw);
    }

    /// <summary>
    /// The Move state's dim (sitting 9 fixup F1): the card's own content — its border and
    /// background, the body text, the number tag and the tail — fades to
    /// <see cref="MoveDimOpacity"/>, while the hint plate and the Move discs stay at full
    /// opacity. The WINDOW is always 1.0: a window-level Opacity multiplies every child, so the
    /// hint (the one thing the owner must read in Move) came out grey at 0.6.
    /// </summary>
    private void UpdateMoveDim()
    {
        double dim = _state == NoteState.Move ? MoveDimOpacity : 1.0;
        Opacity = 1.0;
        if (_cardBorder is not null) _cardBorder.Opacity = dim;
        if (_tail is not null) _tail.Opacity = dim;
        if (_contentBorder is not null) _contentBorder.Opacity = dim;
        if (_badgeTag is not null) _badgeTag.Opacity = dim;
        if (_badgeLabel is not null) _badgeLabel.Opacity = dim;
    }

    /// <summary>The ⌨ Show-keyboard control (slot 1) registers only while the keyboard is hidden (P1 D7);
    /// the manager calls this on entry into Edit and on every keyboard-probe change so the drawn
    /// buttons can never disagree with the registered dwell targets.
    /// </summary>
    public void SetKeyboardControlVisible(bool visible)
    {
        Dispatcher.Invoke(() => SetButtonVisible("keyboard", visible));
    }

    private Brush GetBorderBrush()
    {
        return _state switch
        {
            // Edit and PendingUndo are the active/dwell states -> Accent (cyan). Move is a
            // secondary state that must be distinct from Edit AND clearly visible on surfaces;
            // MarkerMove (a soft violet) clears 3:1 on the note surface where the prior
            // SurfaceKeySwitch mark sat at 1.77-2.18:1 (below the non-text floor).
            NoteState.Edit => Tokens.BrushAccent,
            NoteState.PendingUndo => Tokens.BrushAccent,
            NoteState.Move => Tokens.BrushMarkerMove,
            // T5b: Display's edge is BorderSubtle, not SurfaceMedium — the card edge carries the
            // note's boundary and owes WCAG 1.4.11 3:1 against the page behind the window, which
            // SurfaceMedium fails in every theme (1.1-1.5:1).
            _ => Tokens.BrushBorderSubtle
        };
    }

    private Thickness GetBorderThickness()
    {
        return _state == NoteState.Display ? new Thickness(1) : new Thickness(2);
    }

    private void DrawButtons()
    {
        // ALL sets are drawn once (P1 D7, fixup B1), over the P4 D3 slot table, so the
        // dwell-target math (NoteLayout.ButtonCenter) never changes per state:
        //   Display:      0 edit, 1 move, 2 GAP, 3 close   (P2 D8: close a full cell apart)
        //   Edit:         0 done, 1 show-keyboard (hidden kb only), 2 GAP, 3 discard
        //   Move:         0 other side (only while it fits), 1 cancel
        //   PendingUndo:  0 undo (the chip)
        // UpdateButtonVisibility picks the visible set per state; every other twin is Collapsed,
        // including the cell fills. The fourth field is the button UIA name: the disc is an
        // Ellipse (no automation peer), so the name goes on the peer-bearing TextBlock (D11).
        _displayButtons =
        [
            ("edit",  "✎", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.edit"), 0),
            ("move",  "↕", Tokens.MarkerMove, StringTable.Current.T("overlays.note.move"), 1),
            ("close", "✕", Tokens.AccentDanger, StringTable.Current.T("overlays.note.close"), 3),
        ];
        _editButtons =
        [
            ("done",     "✓", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.done"), 0),
            ("keyboard", "⌨", Tokens.Accent, StringTable.Current.T("overlays.note.showKeyboard"), 1),
            ("discard",  "↩", Tokens.AccentDanger, StringTable.Current.T("overlays.note.discard"), 3),
        ];
        _chipButtons =
        [
            ("undo", "↺", Tokens.Accent, StringTable.Current.T("overlays.note.undo"), 0),
        ];
        _moveButtons =
        [
            ("otherside", "⇄", Tokens.Accent, StringTable.Current.T("overlays.note.otherSide"), 0),
            ("cancel",    "⊘", Tokens.AccentDanger, StringTable.Current.T("overlays.note.cancel"), 1),
        ];

        foreach (var symbols in new[] { _displayButtons, _editButtons, _chipButtons, _moveButtons })
        {
            foreach (var (suffix, symbol, color, accessibleName, slot) in symbols)
            {
                // Window-relative logical centre, shared with NoteManager dwell-target
                // placement - K2a: Edit's controls sit in their row of three cells under the
                // text; every other state reads the column. The side decides the column, and the
                // read rides the tag's top clearance so a grown window draws the cells at the
                // same screen positions.
                var (cx, cy) = _placement is { } bp
                    ? NoteLayout.ButtonCenter(bp, slot, _state)
                    : NoteLayout.ButtonCenter(_side, slot, CardWidthDip);

                var ellipse = new Ellipse
                {
                    Width = ButtonDiameter, Height = ButtonDiameter,
                    Fill = Tokens.BrushSurfaceMedium,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(ellipse, cx - ButtonRadius);
                Canvas.SetTop(ellipse, cy - ButtonRadius);
                _canvas.Children.Add(ellipse);
                _buttons[$"{_noteId}:{suffix}"] = ellipse;
                // T6 fixup-02 B1/m1: the caretaker input is wired to the WHOLE control the way
                // hit-testing and gaze see it — the disc, the glyph label and the 80×80 hit cell
                // (the slot's Rectangle, Collapsed exactly when the slot is unused) all press
                // and release the SAME suffix. The pending-press gate lives in the shared
                // Record/Release helpers below.
                AttachCaretakerInput(ellipse, suffix);

                var label = new TextBlock
                {
                    Text = symbol,
                    // T5 review-02 F2: a label-less gaze disc carries a GAZE-sized glyph - the
                    // DwellArcButton convention for discs (Solo, 0.58 of the 56 DIP disc = about
                    // 32 DIP). Small (0.23, for glyphs BESIDE other content) left a 12.9 DIP
                    // stamp on a 56 DIP target. The family is pinned so the ink math below
                    // measures the same face the TextBlock renders.
                    FontFamily = Tokens.FontStandard,
                    FontSize = ButtonDiameter * ContainerRatio.Solo,
                // T6 fixup-02 B1: the glyph box (32×43 DIP) covers the middle of every 56 DIP
                // disc — hit-testable it swallows every input over the symbol and bubbles to the
                // Canvas, never reaching any wired element. Non hit-testable, a finger at the
                // glyph falls through to the disc. (RaiseEvent on the label still works — no
                // hit-testing involved — so the wiring below covers the review's literal pin.)
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // T5 fixup-03 G1: centre the glyph on its INK, not on its box. A TextBlock box
            // is advance width x line height (~43 DIP tall), so box centring sat every glyph
            // 6-10 DIP low. BuildGeometry gives the ink bounds relative to the text origin,
            // which for an auto-sized TextBlock is the box top-left. The brush is never
            // painted - only the ink geometry is measured; the palette test rejects raw
            // Brush literals in UI files, so it reads a Tokens brush.
            var formatted = new FormattedText(symbol, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(Tokens.FontStandard.Source), label.FontSize,
                Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
            Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            Canvas.SetLeft(label, cx - (ink.Left + (ink.Width / 2)));
            Canvas.SetTop(label, cy - (ink.Top + (ink.Height / 2)));
            if (accessibleName.Length > 0)
                System.Windows.Automation.AutomationProperties.SetName(label, accessibleName);
            _canvas.Children.Add(label);
            _buttonLabels[$"{_noteId}:{suffix}"] = label;
            AttachCaretakerInput(label, suffix);

            if (_cellFills.TryGetValue(NoteLayout.CellRowFor(_state, slot), out var cell))
                AttachCaretakerInput(cell, suffix);

            var arcPath = new Path
            {
                Stroke = Tokens.BrushAccent,
                StrokeThickness = 3, Visibility = Visibility.Hidden,
                // T6 fixup-02 B1: the arc becomes Visible during a dwell — a stroke ring over
                // the disc's rim must not swallow the input under it.
                IsHitTestVisible = false
            };
                Canvas.SetLeft(arcPath, 0); Canvas.SetTop(arcPath, 0);
                _canvas.Children.Add(arcPath);
                _arcs[$"{_noteId}:{suffix}"] = (arcPath, -90);
            }
        }
    }

    private (string Suffix, string Symbol, Color Color, string AccessibleName, int Slot)[] _displayButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName, int Slot)[] _editButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName, int Slot)[] _chipButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName, int Slot)[] _moveButtons = [];

    /// <summary>
    /// Shows or hides the control buttons to match the current state so the visible buttons stay
    /// in sync with the dwell targets registered by <c>NoteManager</c>:
    /// Display = edit+move+close; Edit = Done and Discard always, ⌨ only while the keyboard is
    /// hidden (the manager drives that half through <see cref="SetKeyboardControlVisible"/>);
    /// Move = Other side (only while it fits, the manager drives that through
    /// <see cref="SetOtherSideAvailable"/>) plus Cancel; PendingUndo = the chip's Undo disc.
    /// Every other set EXISTs on the canvas (fixup B1), so every branch must set every key
    /// explicitly — a fresh Ellipse's default Visibility is Visible, and an untouched twin would
    /// render on top of the active set.
    /// </summary>
    private void UpdateButtonVisibility()
    {
        // The P4 D3 slot table is the single truth (NoteLayout.SlotsFor): a state shows exactly
        // its table slots, and every other set EXISTs on the canvas (fixup B1) so every branch
        // must set every key explicitly - a fresh Ellipse default Visibility is Visible, and an
        // untouched twin would render on top of the active set. K2a: Edit's slots map to its
        // row's cells 0, 1 and 2. D: a Display note at rest draws NO control at all - only its
        // reveal target - and its row appears on reveal.
        var occupiedRows = new HashSet<int>();
        foreach (var slot in NoteLayout.DrawnSlotsFor(_state, _revealed))
            occupiedRows.Add(NoteLayout.CellRowFor(_state, slot));
        for (int row = 0; row < 4; row++)
            SetCellVisible(row, occupiedRows.Contains(row));

        if (_state == NoteState.PendingUndo)
        {
            SetButtonVisible("undo", true);
            SetButtonVisible("done", false);
            SetButtonVisible("keyboard", false);
            SetButtonVisible("discard", false);
            SetButtonVisible("close", false);
            SetButtonVisible("edit", false);
            SetButtonVisible("move", false);
            SetButtonVisible("otherside", false);
            SetButtonVisible("cancel", false);
            return;
        }

        if (_state == NoteState.Edit)
        {
            SetButtonVisible("undo", false);
            SetButtonVisible("done", true);
            SetButtonVisible("keyboard", _keyboardControlVisible);
            SetButtonVisible("discard", true);
            SetButtonVisible("close", false);
            SetButtonVisible("edit", false);
            SetButtonVisible("move", false);
            SetButtonVisible("otherside", false);
            SetButtonVisible("cancel", false);
            return;
        }

        SetButtonVisible("undo", false);
        SetButtonVisible("done", false);
        SetButtonVisible("keyboard", false);
        SetButtonVisible("discard", false);
        SetButtonVisible("close", _state == NoteState.Display && _revealed);
        SetButtonVisible("edit", _state == NoteState.Display && _revealed);
        SetButtonVisible("move", _state == NoteState.Display && _revealed);
        // The Move set (P3 D4): Other side shows only while another side fits - the manager
        // drives that through SetOtherSideAvailable; Cancel is always part of Move.
        SetButtonVisible("otherside", _state == NoteState.Move && _otherSideAvailable);
        SetButtonVisible("cancel", _state == NoteState.Move);
    }

    private void SetCellVisible(int row, bool visible)
    {
        if (_cellFills.TryGetValue(row, out var fill))
            fill.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }


    private bool _keyboardControlVisible;
    private bool _otherSideAvailable;

    /// <summary>
    /// The Move state's Other-side control (P3 D4): registered and drawn only while
    /// <c>Place(..., exclude: current)</c> found another fitting side — the manager calls this
    /// on entering Move and on every T1–T6 re-evaluation so the drawn button can never disagree
    /// with the registered dwell target (the same invariant ⌨'s drawn/registered pair pins).
    /// </summary>
    public void SetOtherSideAvailable(bool available)
    {
        _otherSideAvailable = available;
        Dispatcher.Invoke(() => SetButtonVisible("otherside", _state == NoteState.Move && available));
    }

    private void SetButtonVisible(string suffix, bool visible)
    {
        if (suffix == "keyboard") _keyboardControlVisible = visible;
        var key = $"{_noteId}:{suffix}";
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_buttons.TryGetValue(key, out var ellipse)) ellipse.Visibility = vis;
        if (_buttonLabels.TryGetValue(key, out var label)) label.Visibility = vis;
        if (!visible && _arcs.TryGetValue(key, out var arc)) arc.Arc.Visibility = Visibility.Hidden;
    }

    /// <summary>
    /// T6 P5: fires when the caretaker touches or clicks a note control, carrying the control's
    /// suffix; the manager runs the SAME action the control's dwell runs, through the input
    /// policy. The window itself decides nothing — the accept/ignore rule lives in
    /// <see cref="NoteInputPolicy"/> and the registration/visibility inputs come from the
    /// manager, which owns both.
    /// </summary>
    internal event Action<string, NoteInputKind, bool>? ControlInputActivated;

    /// <summary>T6 fixup-02 m1: the pause state a mouse press must be made under. Only the
    /// manager owns the pause seam (<see cref="NoteManager.SetPausePoll"/>), so it forwards the
    /// same poll here at wiring — the window cannot read pause itself, and a mouse press made
    /// while EyeCtrl runs never records (a caretaker clicks a note only while paused).</summary>
    internal Func<bool>? PausePollForInput { get; set; }

    /// <summary>T6 fixup-02 m1: the suffix of the control whose DOWN is still pending a
    /// release. Press and release must land on the SAME control — a finger sliding onto a
    /// control and lifting there acts through nothing without it. Draw() clears it.</summary>
    private string? _pendingCaretakerPress;

    /// <summary>
    /// T6 fixup-03 (review-02 F2): a pending press never survives a release or a leave the
    /// window can see. The bubbling Up clears — with <c>handledEventsToo</c>, so the mark a
    /// control set on its own acted Up does not stop it — consume the press on every release
    /// the routing passes THROUGH the window: TouchUp for touch, the core MouseUp for the
    /// mouse. The core MouseUp is the one route for EVERY button's release on the same
    /// bounding (review-03 n.1: live input also re-raises the direct MouseLeftButtonUp at
    /// every element along the MouseUp route, so the window would see a left-button release
    /// either way — but the core route covers Right/Middle/X buttons on one hook, and
    /// handledEventsToo keeps a marked release from hiding the clear).
    /// Routing order keeps a control's own release first: its handlers run at the element,
    /// the window's after it — the control's ReleaseCaretaker consumes the press, so by the
    /// time the window's Up-clear runs, a release over a wired control has nothing left to clear.
    /// The Leave/capture clears catch a press dragged out of the note, whose lift will never
    /// arrive as an Up anywhere in this tree. The canvas carries the Leave clears separately
    /// because TouchLeave/MouseLeave route DIRECT: a drag that exits the note through the
    /// canvas never reaches the window's own leave.
    /// </summary>
    private void AttachStalePressGuards()
    {
        AddHandler(UIElement.TouchUpEvent,
            new EventHandler<TouchEventArgs>((_, _) => _pendingCaretakerPress = null), handledEventsToo: true);
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, _) => _pendingCaretakerPress = null),
            handledEventsToo: true);

        TouchLeave += (_, _) => _pendingCaretakerPress = null;
        MouseLeave += (_, _) => _pendingCaretakerPress = null;
        LostTouchCapture += (_, _) => _pendingCaretakerPress = null;
        _canvas.TouchLeave += (_, _) => _pendingCaretakerPress = null;
        _canvas.MouseLeave += (_, _) => _pendingCaretakerPress = null;
    }

    private void AttachCaretakerInput(UIElement element, string suffix)
    {
        // Touch, and a paused non-stylus mouse press, record the pending control.
        element.TouchDown += (_, _) => RecordCaretakerPress(suffix);
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice != null) return;
            if ((PausePollForInput?.Invoke() ?? false) == false) return;
            RecordCaretakerPress(suffix);
        };

        // Touch: marked handled so the platform cannot re-report it (a promoted mouse event is
        // ignored downstream anyway — the promotion guard is the policy's business).
        element.TouchUp += (_, e) =>
        {
            e.Handled = true;
            ReleaseCaretaker(suffix, NoteInputKind.Touch, stylus: false);
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            // A touch promotion (StylusDevice != null) is REPORTED, not swallowed: the touch
            // already acted at TouchUp, and the policy ignores the promoted click so the
            // action cannot run a second time.
            ReleaseCaretaker(suffix, NoteInputKind.Mouse, stylus: e.StylusDevice != null);
        };
    }

    /// <summary>m1, press half: a DOWN records the control as pending — but only while the
    /// control is DRAWN. The state rows share one cell Rectangle per slot, so every state's
    /// controls have handlers on the same cell; without the drawn gate, the collapsed twins of
    /// one slot would overwrite the drawn control's pending press.</summary>
    private void RecordCaretakerPress(string suffix)
    {
        if (IsControlDrawn(suffix)) _pendingCaretakerPress = suffix;
    }

    /// <summary>m1, release half: the UP acts only when the pending press belongs to THIS
    /// control, then clears — every Up (matched or not) consumes what is pending, so a stale
    /// press can never act on a later release. The policy (NoteInputPolicy) stays the single
    /// acceptance rule this feed runs into.</summary>
    private void ReleaseCaretaker(string suffix, NoteInputKind kind, bool stylus)
    {
        var pressed = _pendingCaretakerPress;
        _pendingCaretakerPress = null;
        if (pressed != suffix) return;
        ControlInputActivated?.Invoke(suffix, kind, stylus);
    }

    /// <summary>Whether the named control is currently DRAWN visible (its ellipse is not
    /// Collapsed). One of the policy's two per-control inputs: a control the window has not
    /// drawn never acts, however it is touched or clicked.</summary>
    internal bool IsControlDrawn(string suffix) =>
        _buttons.TryGetValue($"{_noteId}:{suffix}", out var ellipse)
        && ellipse.Visibility == Visibility.Visible;

    /// <summary>Test accessors (T6): the drawn tag elements, or nulls when none is drawn.</summary>
    internal Border? BadgeTagForTest => _badgeTag;
    internal TextBlock? BadgeLabelForTest => _badgeLabel;

    public void UpdateDwellProgress(string targetId, double progress)
    {
        if (!_buttons.TryGetValue(targetId, out var ellipse)) return;
        if (!_arcs.TryGetValue(targetId, out var arcData)) return;

        var (arcPath, startAngle) = arcData;
        Dispatcher.Invoke(() =>
        {
            if (progress <= 0)
            {
                arcPath.Visibility = Visibility.Hidden;
                return;
            }
            arcPath.Visibility = Visibility.Visible;
            double cx = Canvas.GetLeft(ellipse) + ButtonRadius;
            double cy = Canvas.GetTop(ellipse) + ButtonRadius;
            double r = ButtonRadius - 2;

            var arc = new ArcSegment
            {
                Size = new Size(r, r),
                SweepDirection = SweepDirection.Clockwise
            };
            DwellArcButton.UpdateArc(arc, new Point(cx, cy), r, progress);

            var fig = new PathFigure
            {
                StartPoint = new Point(cx + r * Math.Cos(startAngle * Math.PI / 180), cy + r * Math.Sin(startAngle * Math.PI / 180))
            };
            fig.Segments.Add(arc);
            arcPath.Data = new PathGeometry(new[] { fig });
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Loaded -= OnLoaded;
    }
}
