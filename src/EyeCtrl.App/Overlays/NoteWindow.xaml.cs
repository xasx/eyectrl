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
    private Border? _contentBorder;
    private NoteState _state;

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
        Width = LayoutConstants.NoteWindowWidth;
        Height = LayoutConstants.NoteWindowHeight;

        _canvas = new Canvas { ClipToBounds = false };
        Content = _canvas;
        Loaded += OnLoaded;
    }

    // The show seam (T6 fixup-08 R4, moved down by sync 4): set when the manager skipped the HWND show.
    private bool _displayedWithoutHwnd;

    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false -
    /// performs what <see cref="Window.Show"/> builds for the rest of the app (the drawn tree,
    /// which the Loaded handler would have built) without ever putting a window on the screen.
    /// The headless tests run on the OWNER's machine, where a topmost note window is exactly what
    /// must not appear; the placement, the bounds, the target registrations and
    /// <see cref="IsDisplayed"/> behave as if the window had been shown.
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
    /// or the seam's own flag when the window was never handed to the OS.</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;

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

        var bg = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height,
            Background = Tokens.BrushSurfaceDark,
            CornerRadius = Tokens.StandardCornerRadius,
            BorderBrush = GetBorderBrush(),
            BorderThickness = GetBorderThickness(),
            // StrokeHalo is the palette's near-black; at 0.4 opacity it is indistinguishable
            // from pure black, and it keeps the shadow on the token so a light theme can move it.
            Effect = new DropShadowEffect { Color = Tokens.StrokeHalo, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        _canvas.Children.Add(bg);

        var titleBar = new Border
        {
            Height = TitleBarHeight, Width = LayoutConstants.NoteWindowWidth,
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
        Canvas.SetLeft(titleBar, 0); Canvas.SetTop(titleBar, 0);
        _canvas.Children.Add(titleBar);

        _contentBorder = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height - TitleBarHeight,
            Background = Brushes.Transparent
        };
        Canvas.SetLeft(_contentBorder, 0); Canvas.SetTop(_contentBorder, TitleBarHeight);
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
    /// no longer exists on any path.
    /// </summary>
    public void UpdateContent()
    {
        if (_contentBorder == null) return;
        _contentBorder.Child = null;

        // Each state keeps its previous text colour: Display's amber body and Edit's primary
        // editor text are today's look, and restyling them is T5's scope, not this ticket's.
        _textBlock = new TextBlock
        {
            Width = TextAreaWidth - 20,
            MaxWidth = TextAreaWidth - 20,
            Margin = new Thickness(10, 10, 0, 0),
            FontSize = Tokens.FontSizeBody,
            Foreground = _state == NoteState.Edit ? Tokens.BrushTextPrimary : Tokens.BrushAccentWarning,
            TextWrapping = TextWrapping.Wrap
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

    public void SetText(string text)
    {
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
            Opacity = state == NoteState.Move ? 0.85 : 1.0;
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
        if (_canvas.Children.Count == 0) return;
        if (_canvas.Children[0] is Border bg)
        {
            bg.BorderBrush = GetBorderBrush();
            bg.BorderThickness = GetBorderThickness();
        }
    });

    private Brush GetBorderBrush()
    {
        return _state switch
        {
            // Edit is the active/dwell state -> Accent (cyan). Move is a secondary state that
            // must be distinct from Edit AND clearly visible on surfaces; MarkerMove (a soft
            // violet) clears 3:1 on the note surface where the prior SurfaceKeySwitch mark sat
            // at 1.77-2.18:1 (below the non-text floor).
            NoteState.Edit => Tokens.BrushAccent,
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
        // BOTH sets are drawn once, over the same three slots (P1 D7, fixup B1), so Done and
        // (in P2) Discard stay one slot apart and the dwell-target math (NoteLayout.ButtonCenter)
        // never changes:
        //   Display: 0 ✕ close, 1 ✎ edit, 2 ↕ move
        //   Edit:    0 ✓ Done (AccentSuccess), 1 ⌨ Show keyboard (Accent, keyboard-hidden only), 2 empty
        // Every note is created in Edit before Show(), so a canvas that drew only the current
        // state's set could never gain the Display controls later — SetState(Display) would
        // unhide buttons that do not exist while their dwell targets go live. UpdateButtonVisibility
        // picks the visible set per state; the hidden twin of each slot is Collapsed.
        // The fourth field is the button's UIA name: the disc is an Ellipse (no automation peer),
        // and the glyph label would otherwise announce as its own symbol, so the name goes on the
        // peer-bearing TextBlock (D11).
        _displayButtons =
        [
            ("close", "✕", Tokens.AccentDanger, StringTable.Current.T("overlays.note.close")),
            ("edit",  "✎", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.edit")),
            ("move",  "↕", Tokens.MarkerMove, StringTable.Current.T("overlays.note.move")),
        ];
        _editButtons =
        [
            ("done",     "✓", Tokens.AccentSuccess, StringTable.Current.T("overlays.note.done")),
            ("keyboard", "⌨", Tokens.Accent, StringTable.Current.T("overlays.note.showKeyboard")),
            ("none",     "",  Tokens.AccentDanger, string.Empty),
        ];

        foreach (var symbols in new[] { _displayButtons, _editButtons })
        {
            for (int i = 0; i < symbols.Length; i++)
            {
                var (suffix, symbol, color, accessibleName) = symbols[i];
                // Window-relative logical center, shared with NoteManager's dwell-target placement.
                var (cx, cy) = NoteLayout.ButtonCenter(i);

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

    /// <summary>
    /// Shows or hides the control buttons to match the current state so the visible buttons stay
    /// in sync with the dwell targets registered by <c>NoteManager</c>:
    /// Display = close+edit+move; Edit = Done always, ⌨ only while the keyboard is hidden (the
    /// manager drives that half through <see cref="SetKeyboardControlVisible"/>); Move = none.
    /// </summary>
    private void UpdateButtonVisibility()
    {
        // Both sets EXIST on the canvas now (fixup B1), so every branch must set every key
        // explicitly — a fresh Ellipse's default Visibility is Visible, and an untouched twin
        // would render on top of the active set.
        if (_state == NoteState.Edit)
        {
            SetButtonVisible("done", true);
            SetButtonVisible("keyboard", _keyboardControlVisible);
            SetButtonVisible("close", false);
            SetButtonVisible("edit", false);
            SetButtonVisible("move", false);
            SetButtonVisible("none", false);
            return;
        }

        SetButtonVisible("done", false);
        SetButtonVisible("keyboard", false);
        SetButtonVisible("close", _state != NoteState.Move);
        SetButtonVisible("edit", _state == NoteState.Display);
        SetButtonVisible("move", _state == NoteState.Display);
        SetButtonVisible("none", false);
    }

    private bool _keyboardControlVisible;

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
