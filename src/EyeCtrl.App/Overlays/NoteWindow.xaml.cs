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
using EyeCtrl.Core.Models;

public class NoteWindow : OverlayWindow
{
    private const double ButtonRadius = LayoutConstants.NoteButtonRadius;
    private const double ButtonDiameter = ButtonRadius * 2;
    private const double ButtonGap = LayoutConstants.NoteButtonGap;
    private const double ButtonPadding = LayoutConstants.NoteButtonPadding;
    private const double TitleBarHeight = LayoutConstants.NoteTitleBarHeight;
    private const double TextAreaWidth = 200;

    private readonly string _noteId;
    private readonly Canvas _canvas;
    private readonly Dictionary<string, Ellipse> _buttons = new();
    private readonly Dictionary<string, TextBlock> _buttonLabels = new();
    private readonly Dictionary<string, (Path Arc, double StartAngle)> _arcs = new();
    private TextBlock? _textBlock;
    private TextBlock? _chipTextBlock;
    private Border? _contentBorder;
    private NoteState _state;
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
        Width = LayoutConstants.NoteWindowWidth;
        Height = LayoutConstants.NoteWindowHeight;

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

        // P3 D2: the card sits inside the window at the placement's card origin (the tail zone
        // takes the strip on the anchor's side); the pointer tail is drawn INSIDE the window.
        var cardLocal = _placement is { } p ? NoteLayout.RectLocal(p) : new Rect(0, 0, LayoutConstants.NoteWindowWidth, LayoutConstants.NoteWindowHeight);
        var borderBrush = GetBorderBrush();

        if (_placement?.TailVisible == true)
        {
            // The tail is NOT an offer zone (P2 D7): it is drawn under the card's z-order and
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
            // StrokeHalo is the palette's near-black; at 0.4 opacity it is indistinguishable
            // from pure black, and it keeps the shadow on the token so a light theme can move it.
            Effect = new DropShadowEffect { Color = Tokens.StrokeHalo, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, cardLocal.Left); Canvas.SetTop(bg, cardLocal.Top);
        _canvas.Children.Add(bg);

        var titleBar = new Border
        {
            Height = TitleBarHeight, Width = cardLocal.Width,
            Background = Tokens.BrushSurfaceMedium,
            // Top-only radius: the title bar sits flush against the note body below it.
            // Derived from the same token, so it cannot drift from the body's corners.
            CornerRadius = new CornerRadius(
                Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard, 0, 0)
        };
        var titleLabel = new TextBlock
        {
            Text = StringTable.Current.T("overlays.note.title"), FontSize = Tokens.FontSizeBody,
            Foreground = Tokens.BrushTextSecondary,
            Margin = new Thickness(8, 4, 0, 0)
        };
        titleBar.Child = titleLabel;
        Canvas.SetLeft(titleBar, cardLocal.Left); Canvas.SetTop(titleBar, cardLocal.Top);
        _canvas.Children.Add(titleBar);

        _contentBorder = new Border
        {
            Width = cardLocal.Width, Height = cardLocal.Height - TitleBarHeight,
            Background = Brushes.Transparent
        };
        Canvas.SetLeft(_contentBorder, cardLocal.Left); Canvas.SetTop(_contentBorder, cardLocal.Top + TitleBarHeight);
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

        if (_state == NoteState.PendingUndo)
        {
            _chipTextBlock = new TextBlock
            {
                Width = TextAreaWidth - 20,
                MaxWidth = TextAreaWidth - 20,
                Margin = new Thickness(10, 10, 0, 0),
                FontSize = Tokens.FontSizeBody,
                Foreground = Tokens.BrushTextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Text = _chipText
            };
            _contentBorder.Child = _chipTextBlock;
            return;
        }

        // Each state keeps its previous text colour: Display's amber body and Edit's primary
        // editor text are today's look, and restyling them is T5's scope, not this ticket's.
        _textBlock = new TextBlock
        {
            Width = TextAreaWidth - 20,
            MaxWidth = TextAreaWidth - 20,
            Margin = new Thickness(10, 10, 0, 0),
            FontSize = Tokens.FontSizeBody,
            Foreground = _state == NoteState.Edit ? Tokens.BrushTextPrimary : Tokens.BrushAccentWarning,
            TextWrapping = TextWrapping.Wrap,
            Text = _text
        };

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
    }

    /// <summary>Sets the Undo chip's text (P2 D5) and re-renders when the chip is showing. The
    /// text comes from the manager through the localization table, kind-dependent.</summary>
    public void SetChipText(string text)
    {
        _chipText = text;
        if (_state == NoteState.PendingUndo && _chipTextBlock != null)
            _chipTextBlock.Text = text;
    }

    public void SetText(string text)
    {
        _text = text;
        if (_textBlock != null) _textBlock.Text = text;
    }

    public string GetText() => _textBlock?.Text ?? "";

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
        // ALL sets are drawn once, over the same three slots (P1 D7, fixup B1; T3 P2 D8), so the
        // dwell-target math (NoteLayout.ButtonCenter) never changes per state:
        //   Display:      0 ✎ edit, 1 ↕ move, 2 ✕ close  (P2 D8: ✕ sits apart, bottom slot)
        //   Edit:         0 ✓ Done (AccentSuccess), 1 ⌨ Show keyboard (Accent, keyboard-hidden
        //                 only), 2 ↩ Discard (AccentDanger) — Done and Discard stay one slot apart
        //   PendingUndo:  0 ↺ Undo — the chip's only control (P2 D5)
        // Every note is created in Edit before Show(), so a canvas that drew only the current
        // state's set could never gain the Display controls later — SetState(Display) would
        // unhide buttons that do not exist while their dwell targets go live.
        // UpdateButtonVisibility picks the visible set per state; every other slot's twin is
        // Collapsed. The fourth field is the button's UIA name: the disc is an Ellipse (no
        // automation peer), and the glyph label would otherwise announce as its own symbol, so
        // the name goes on the peer-bearing TextBlock (D11).
        _displayButtons =
        [
            ("edit",  "✎", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.edit")),
            ("move",  "↕", Tokens.MarkerMove, StringTable.Current.T("overlays.note.move")),
            ("close", "✕", Tokens.AccentDanger, StringTable.Current.T("overlays.note.close")),
        ];
        _editButtons =
        [
            ("done",     "✓", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.done")),
            ("keyboard", "⌨", Tokens.Accent, StringTable.Current.T("overlays.note.showKeyboard")),
            ("discard",  "↩", Tokens.AccentDanger, StringTable.Current.T("overlays.note.discard")),
        ];
        _chipButtons =
        [
            ("undo", "↺", Tokens.Accent, StringTable.Current.T("overlays.note.undo")),
        ];
        // The Move state's set (P3 D4): Other side and Cancel, slot 0 and 1 — drawn once like
        // every other set so the dwell-target math never changes per state. UpdateButtonVisibility
        // shows Other side only while another side actually fits; the manager drives that half.
        _moveButtons =
        [
            ("otherside", "⇄", Tokens.Accent, StringTable.Current.T("overlays.note.otherSide")),
            ("cancel",    "⊘", Tokens.AccentDanger, StringTable.Current.T("overlays.note.cancel")),
        ];

        foreach (var symbols in new[] { _displayButtons, _editButtons, _chipButtons, _moveButtons })
        {
            for (int i = 0; i < symbols.Length; i++)
            {
                var (suffix, symbol, color, accessibleName) = symbols[i];
                // Window-relative logical centre, shared with NoteManager's dwell-target
                // placement — column side follows the placement side (P3 D2.6).
                var (cx, cy) = NoteLayout.ButtonCenter(_side, i);

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
                    // Sized from the 60px button disc that bounds it (ButtonDiameter above).
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

    private (string Suffix, string Symbol, Color Color, string AccessibleName)[] _displayButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName)[] _editButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName)[] _chipButtons = [];
    private (string Suffix, string Symbol, Color Color, string AccessibleName)[] _moveButtons = [];

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
        SetButtonVisible("close", _state != NoteState.Move);
        SetButtonVisible("edit", _state == NoteState.Display);
        SetButtonVisible("move", _state == NoteState.Display);
        // The Move set (P3 D4): Other side shows only while another side fits — the manager
        // drives that through SetOtherSideAvailable; Cancel is always part of Move.
        SetButtonVisible("otherside", _state == NoteState.Move && _otherSideAvailable);
        SetButtonVisible("cancel", _state == NoteState.Move);
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
