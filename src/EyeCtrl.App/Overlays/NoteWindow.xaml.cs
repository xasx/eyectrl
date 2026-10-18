namespace EyeCtrl.App.Overlays;

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
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Applies a derived placement (P3 D2): resizes the window to the placement's window rect
    /// (card + tail zone) and redraws everything — the column follows the side, the tail appears
    /// beside the card. The manager sets <c>Left</c>/<c>Top</c> from the same placement, so the
    /// drawn geometry and the window position can never disagree. Safe before Show(): Draw is
    /// idempotent and the Loaded handler re-runs it. Internal: <see cref="NotePlacement"/> is,
    /// and only the manager (same assembly) applies placements.
    /// </summary>
    internal void ApplyPlacement(NotePlacement placement)
    {
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
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
            // ChromeRects never reports it. Same surface fill, same border colour.
            var tail = new Polygon
            {
                Points = new PointCollection(NoteLayout.TailTriangle(_placement)!),
                Fill = Tokens.BrushSurfaceDark,
                Stroke = borderBrush,
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
            var (cx, cy) = NoteLayout.ButtonCenter(_side, row, CardWidthDip);
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
            Dispatcher.Invoke(UpdateContent);
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
            _ => Tokens.BrushSurfaceMedium
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
                // 1:1 to its hit-cell row (P4 D3).
                var (cx, cy) = NoteLayout.ButtonCenter(_side, slot, CardWidthDip);

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

                var label = new TextBlock
                {
                    Text = symbol,
                    // Sized from the 56px disc that bounds it (ButtonDiameter above).
                    FontSize = ButtonDiameter * ContainerRatio.Small,
                    Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Canvas.SetLeft(label, cx - 8);
                Canvas.SetTop(label, cy - 10);
                if (accessibleName.Length > 0)
                    System.Windows.Automation.AutomationProperties.SetName(label, accessibleName);
                _canvas.Children.Add(label);
                _buttonLabels[$"{_noteId}:{suffix}"] = label;

                var arcPath = new Path
                {
                    Stroke = Tokens.BrushAccent,
                    StrokeThickness = 3, Visibility = Visibility.Hidden
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
