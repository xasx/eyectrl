namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Linq;
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
    private NoteState _state;
    // T6 fixup-01: the tag elements are tracked so DrawBadge can replace them without a full Draw.
    private Border? _badgeTag;
    private TextBlock? _badgeLabel;
    private int? _badgeNumber;
    // The note body text size in DIP (P4 D1): pushed by the manager at creation and on every
    // T8 raise; every text measurement and drawn font derives from it.
    private double _textSize = NoteSettings.DefaultTextSizeDip;

    private string _chipText = string.Empty;
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
    /// seam grows the same way and the tag never draws outside the window bounds.
    /// </para>
    /// </summary>
    internal void ApplyPlacement(NotePlacement placement)
    {
        placement = NoteLayout.WithTagClearance(placement);
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
    /// <summary>Test accessor (fixup-01 G3): the drawn buttons, keyed per note id and set slot,
    /// so the window-geometry pin can check the canvas against NoteLayout.ButtonCenter.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, Ellipse> ButtonsForTest() => _buttons;

    internal System.Collections.Generic.IReadOnlyDictionary<string, TextBlock> LabelsForTest() => _buttonLabels;
    internal double ButtonRadiusForTest => ButtonRadius;

    /// <summary>Test accessor (T6 fixup-02): the drawn cell fill of a hit-cell row, the element
    /// the caretaker input is wired to beside the disc and label — or null when the row never
    /// got one.</summary>
    internal Rectangle? CellFillForTest(int row) => _cellFills.TryGetValue(row, out var fill) ? fill : null;

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

        // P3 D2: the card sits inside the window at the placement card origin (the tail zone
        // takes the strip on the anchor side); the pointer tail is drawn INSIDE the window.
        // P4 D2: no title bar - the card is body text plus the control column.
        var cardLocal = _placement is { } p ? NoteLayout.RectLocal(p)
            : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
        var borderBrush = GetBorderBrush();

        if (_placement?.TailVisible == true)
        {
            // The tail is NOT an offer zone (P2 D7): it is drawn under the card z-order and
            // ChromeRects never reports it. Its stroke is BorderSubtle in EVERY state (T5b):
            // the tail shows the caretaker what the note points at, so it owes the same WCAG
            // 1.4.11 3:1 the card edge owes, against a page of either lightness — and no state
            // brush gives that (SurfaceMedium sits at 1.1-1.5:1, the Accent/MarkerMove marks
            // carry state meaning the tail must not repeat). Constant on purpose, not
            // borderBrush: UpdateBorder only refreshes the card Border, never this polygon, so
            // a state-varying stroke here would go stale on every SetState.
            var tail = new Polygon
            {
                Points = new PointCollection(NoteLayout.TailTriangle(_placement)!),
                Fill = Tokens.BrushSurfaceDark,
                Stroke = Tokens.BrushBorderSubtle,
                StrokeThickness = 1
            };
            _canvas.Children.Add(tail);
        }

        var bg = new Border
        {
            Width = cardLocal.Width, Height = cardLocal.Height,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = borderBrush,
            BorderThickness = GetBorderThickness(),
            // StrokeHalo is the palette near-black; at 0.4 opacity it is indistinguishable
            // from pure black, and it keeps the shadow on the token so a light theme can move it.
            Effect = new DropShadowEffect { Color = Tokens.StrokeHalo, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, cardLocal.Left); Canvas.SetTop(bg, cardLocal.Top);
        _canvas.Children.Add(bg);

        // P4 D3: every hit-cell row gets its alpha-1 fill (P2 D2 layout rule 2 - the hit area
        // sits on alpha > 0 pixels even where its disc does not reach). Drawn once for ALL rows
        // like the buttons; UpdateButtonVisibility picks the state rows.
        for (int row = 0; row < 4; row++)
        {
            var (cx, cy) = _placement is { } wp
                ? NoteLayout.ButtonCenter(wp, row)
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
            Width = cardLocal.Width, Height = cardLocal.Height,
            Background = Brushes.Transparent
        };
        Canvas.SetLeft(_contentBorder, cardLocal.Left); Canvas.SetTop(_contentBorder, cardLocal.Top);
        _canvas.Children.Add(_contentBorder);

        DrawButtons();
        UpdateContent();
        UpdateButtonVisibility();
        DrawBadge();
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
            return;

        // Position: straddling the card's TOP edge at the TEXT-side end (the column hugs the
        // edge away from the tail), inset from the corner; on a Below placement the tail shares
        // the top edge, so the tag slides toward the column to clear the polygon. The window
        // grew upward by the protrusion when the badge pushed (WithTagClearance), so the top
        // half has room; the card's screen rect never moved.
        var rect = NoteLayout.TagRect(placement);

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
        // asymmetric advance boxes, and box centring reads off-centre. The ratio is Solo —
        // the glyph is the tag's entire content (the same bucket the control discs read).
        var label = new TextBlock
        {
            Text = number.ToString(CultureInfo.InvariantCulture),
            FontFamily = Tokens.FontStandard,
            FontSize = NoteLayout.TagHeightDip * ContainerRatio.Solo,
            Foreground = Tokens.BrushSurfaceDark,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var formatted = new FormattedText(label.Text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(Tokens.FontStandard.Source), label.FontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        double cx = rect.Left + (rect.Width / 2);
        double cy = rect.Top + (rect.Height / 2);
        Canvas.SetLeft(label, cx - (ink.Left + (ink.Width / 2)));
        Canvas.SetTop(label, cy - (ink.Top + (ink.Height / 2)));
        _canvas.Children.Add(label);
        _badgeLabel = label;
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
        OverCapForTest = false;
        OverCapFadeForTest = false;

        if (_state == NoteState.PendingUndo)
        {
            // The chip (P4 D3, styling final): the Undo cell plus the chip text at the body size,
            // in the shrunk-in-place window.
            _chipTextBlock = new TextBlock
            {
                Width = NoteLayout.ChipTextWidthDip,
                MaxWidth = NoteLayout.ChipTextWidthDip,
                Margin = new Thickness(10, 10, 0, 0),
                FontSize = Tokens.FontSizeBody,
                Foreground = Tokens.BrushTextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Text = _chipText
            };
            _contentBorder.Child = _chipTextBlock;
            // B1: beside the column here too.
            var chipCard = _placement is { } cp ? NoteLayout.RectLocal(cp)
                : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
            _contentBorder.Width = NoteLayout.ChipTextWidthDip + ButtonPadding;
            Canvas.SetLeft(_contentBorder, _side == NoteSide.Left
                ? chipCard.Left + LayoutConstants.NoteColumnWidthDip
                : chipCard.Left);
            Canvas.SetTop(_contentBorder, chipCard.Top);
            return;
        }

        // P4 D2: one primary-colour body for Display and Edit alike (the amber Display accent is
        // gone with the title bar). Over the cap, Display clips from the TOP with a fade and an
        // ellipsis; Edit clips to the TAIL of the draft, where the end marker sits.
        double avail = TextAreaHeightDip();
        bool overCap = MeasureTextHeightDip(_text) > avail + 0.5;
        string shown = overCap
            ? _state == NoteState.Edit
                ? TailThatFits(_text, avail)
                : HeadThatFits(_text, Math.Max(1, avail - NoteLayout.TextLineHeightDip(_textSize))) + "…"
            : _text;

        _textBlock = new TextBlock
        {
            Width = NoteLayout.TextColumnWidthDip(_textSize),
            MaxWidth = NoteLayout.TextColumnWidthDip(_textSize),
            Margin = new Thickness(10, 10, 0, 0),
            FontSize = _textSize,
            FontFamily = Tokens.FontStandard,
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
            // bar states plainly that typed text lands after everything shown.
            var endMarker = new Rectangle
            {
                Width = ButtonRadius / 3,
                Height = ButtonRadius / 3,
                Fill = Tokens.BrushAccent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(10, 2, 0, 0)
            };
            stack.Children.Add(endMarker);
        }

        _contentBorder.Child = stack;

        // T5 review-01 B1: the text sits BESIDE the control column, never under it. The column
        // hugs the card edge away from the tail, so the text area takes the remaining strip.
        var card = _placement is { } p ? NoteLayout.RectLocal(p)
            : new Rect(0, 0, NoteLayout.CardWidthDip(_textSize), NoteLayout.ControlsColumnHeightDip);
        bool columnOnLeft = _side == NoteSide.Left;
        _contentBorder.Width = NoteLayout.TextColumnWidthDip(_textSize) + ButtonPadding;
        Canvas.SetLeft(_contentBorder, columnOnLeft ? card.Left + LayoutConstants.NoteColumnWidthDip : card.Left);
        Canvas.SetTop(_contentBorder, card.Top);
    }

    /// <summary>Test seams (P4 acceptance): whether the current render clips over the cap, and
    /// whether the fade+ellipsis treatment is applied (Display only).</summary>
    internal bool OverCapForTest { get; private set; }

    internal bool OverCapFadeForTest { get; private set; }

    /// <summary>The text area's available height in DIP: the card inner height minus the
    /// paddings. The cap itself rode the placement - the manager derived the card height from
    /// min(55 percent of the work area, 12 lines) - so the window clips to whatever fits it.</summary>
    private double TextAreaHeightDip()
    {
        double h = _placement is { } p ? NoteLayout.RectLocal(p).Height : NoteLayout.ControlsColumnHeightDip;
        return Math.Max(1, h - (2 * ButtonPadding));
    }

    /// <summary>Measures the wrapped text height in DIP the auto-height derives from (P4 D2):
    /// the same style the card body draws, measured at the text column width.</summary>
    internal double MeasureTextHeightDip(string text)
    {
        var block = new TextBlock
        {
            FontSize = _textSize,
            FontFamily = Tokens.FontStandard,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = NoteLayout.TextLineHeightDip(_textSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = text,
            Width = NoteLayout.TextColumnWidthDip(_textSize)
        };
        block.Measure(new Size(NoteLayout.TextColumnWidthDip(_textSize), double.PositiveInfinity));
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
    /// with the text, so a live block re-renders.</summary>
    public void SetText(string text)
    {
        _text = text;
        if (_textBlock != null)
            Dispatcher.Invoke(UpdateContent);
    }

    /// <summary>Pushes the text size (P4 D1/T8). Every measurement and the drawn font follow.</summary>
    public void SetTextSize(double textSize)
    {
        _textSize = textSize;
        if (_contentBorder != null)
            Dispatcher.Invoke(() =>
            {
                UpdateContent();
                // T8: the tag is size-independent (fixed 28x18 DIP), but its rect derives from
                // the placement — ApplyPlacementToWindow skips identical rects, so Draw would
                // not run and a placement-inflight read must not go stale.
                DrawBadge();
            });
    }


    public string GetText() => _textBlock?.Text ?? "";

    /// <summary>Sets the Undo chip text (P2 D5) and re-renders when the chip is showing. The
    /// text comes from the manager through the localization table, kind-dependent.</summary>
    public void SetChipText(string text)
    {
        _chipText = text;
        if (_state == NoteState.PendingUndo && _chipTextBlock != null)
            _chipTextBlock.Text = text;
    }

    public void SetState(NoteState state)
    {
        _state = state;
        Dispatcher.Invoke(() =>
        {
            UpdateContent();
            UpdateBorder();
            UpdateButtonVisibility();
            // P3 D4: Move dims the card in place to 0.6 — the 0.85 of the gaze-chasing Move is
            // gone with the follow loop; the card must visibly yield while its spot is decided.
            Opacity = state == NoteState.Move ? 0.6 : 1.0;
            // No SetOverlayInteraction call on any path (P1 D3): the base class already pinned
            // click-through:false + focusable:false in OnSourceInitialized, and the window stays
            // WS_EX_NOACTIVATE for life.
        });
    }

    /// <summary>
    /// The ⌨ Show-keyboard control (slot 1) registers only while the keyboard is hidden (P1 D7);
    /// the manager calls this on entry into Edit and on every keyboard-probe change so the drawn
    /// buttons can never disagree with the registered dwell targets.
    /// </summary>
    public void SetKeyboardControlVisible(bool visible)
    {
        Dispatcher.Invoke(() => SetButtonVisible("keyboard", visible));
    }

    private void UpdateBorder() => Dispatcher.Invoke(() =>
    {
        // P3 D2: the tail polygon may occupy slot 0 (it is drawn under the card), so the bg
        // border is found by type, not by index.
        var bg = _canvas.Children.OfType<Border>().FirstOrDefault();
        if (bg != null)
        {
            bg.BorderBrush = GetBorderBrush();
            bg.BorderThickness = GetBorderThickness();
        }
    });

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
                // placement - column side follows the placement side (P3 D2.6), the slot maps
                // 1:1 to its hit-cell row (P4 D3); the read rides the tag's top clearance so
                // a grown window draws the cells at the same screen positions.
                var (cx, cy) = _placement is { } bp
                    ? NoteLayout.ButtonCenter(bp, slot)
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

            if (_cellFills.TryGetValue(slot, out var cell))
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
        // untouched twin would render on top of the active set.
        var slots = NoteLayout.SlotsFor(_state);
        bool occupied(int slot) => slots.Contains(slot);

        SetCellVisible(0, occupied(0));
        SetCellVisible(1, occupied(1));
        SetCellVisible(2, occupied(2));
        SetCellVisible(3, occupied(3));

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
        SetButtonVisible("close", _state == NoteState.Display);
        SetButtonVisible("edit", _state == NoteState.Display);
        SetButtonVisible("move", _state == NoteState.Display);
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
    /// mouse (the button-specific MouseLeftButtonUp routes DIRECT — it never reaches an
    /// ancestor — so a release over the card body can only be observed on the core route).
    /// Routing order keeps a control's own release first: its handlers run at the element,
    /// the window's after it — and the core mouse clear does not take a press away from a
    /// release ON a wired control either, because the direct left-button release that follows
    /// it inside the same pipeline clears and acts through that control.
    /// The Leave/capture clears catch a press dragged out of the note, whose lift will never
    /// arrive as an Up anywhere in this tree. The canvas carries the Leave clears separately
    /// because TouchLeave/MouseLeave route DIRECT: a drag that exits the note through the
    /// canvas never reaches the window's own leave.
    /// </summary>
    private void AttachStalePressGuards()
    {
        AddHandler(UIElement.TouchUpEvent,
            new EventHandler<TouchEventArgs>((_, _) => _pendingCaretakerPress = null), handledEventsToo: true);
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(OnBubblingMouseUp), handledEventsToo: true);

        TouchLeave += (_, _) => _pendingCaretakerPress = null;
        MouseLeave += (_, _) => _pendingCaretakerPress = null;
        LostTouchCapture += (_, _) => _pendingCaretakerPress = null;
        _canvas.TouchLeave += (_, _) => _pendingCaretakerPress = null;
        _canvas.MouseLeave += (_, _) => _pendingCaretakerPress = null;
    }

    /// <summary>The core mouse release bubble: keep the press alive when the release is over a
    /// wired control (its own release consumes it, act-or-not), take it otherwise — over the
    /// card body, the chrome, wherever no control handler will run.</summary>
    private void OnBubblingMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is UIElement u &&
            (_buttons.Values.Any(b => ReferenceEquals(b, u)) || _cellFills.Values.Any(c => ReferenceEquals(c, u))))
            return;
        _pendingCaretakerPress = null;
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
