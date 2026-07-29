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
    private TextBox? _textBox;
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
            CornerRadius = new CornerRadius(6),
            BorderBrush = GetBorderBrush(),
            BorderThickness = GetBorderThickness(),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        _canvas.Children.Add(bg);

        var titleBar = new Border
        {
            Height = TitleBarHeight, Width = LayoutConstants.NoteWindowWidth,
            Background = Tokens.BrushSurfaceMedium,
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };
        var titleLabel = new TextBlock
        {
            Text = "PINNED NOTE", FontSize = 10,
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

    public void UpdateContent()
    {
        if (_contentBorder == null) return;
        _contentBorder.Child = null;

        if (_state == NoteState.Edit)
        {
            _textBlock = null;
            _textBox = new TextBox
            {
                Width = TextAreaWidth - 20, Height = Height - TitleBarHeight - 20,
                Margin = new Thickness(10, 10, 0, 0),
                Background = Tokens.BrushSurfaceKey,
                Foreground = Tokens.BrushTextPrimary,
                BorderBrush = Tokens.BrushAccent,
                BorderThickness = new Thickness(1),
                FontSize = 12,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _contentBorder.Child = _textBox;
        }
        else
        {
            _textBox = null;
            _textBlock = new TextBlock
            {
                Width = TextAreaWidth - 20,
                MaxWidth = TextAreaWidth - 20,
                Margin = new Thickness(10, 10, 0, 0),
                FontSize = 12,
                Foreground = Tokens.BrushAccentWarning,
                TextWrapping = TextWrapping.Wrap
            };
            _contentBorder.Child = _textBlock;
        }
    }

    public void SetText(string text)
    {
        if (_textBlock != null) _textBlock.Text = text;
        if (_textBox != null) _textBox.Text = text;
    }

    public string GetText()
    {
        if (_textBox != null) return _textBox.Text;
        return _textBlock?.Text ?? "";
    }

    /// <summary>
    /// Activates the window and moves keyboard focus into the editor TextBox so that both
    /// physical-keyboard input and eye-keyboard <c>SendInput</c> text land in the note.
    /// Must be called after the window has been shown and made focusable.
    /// </summary>
    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            if (_textBox != null)
            {
                _textBox.Focus();
                Keyboard.Focus(_textBox);
                _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void SetState(NoteState state)
    {
        _state = state;
        Dispatcher.Invoke(() =>
        {
            UpdateContent();
            UpdateBorder();
            UpdateButtonVisibility();
            Opacity = state == NoteState.Move ? 0.85 : 1.0;

            if (state == NoteState.Edit)
            {
                SetOverlayInteraction(clickThrough: false, focusable: true);
                _textBox?.Focus();
            }
            else
            {
                SetOverlayInteraction(clickThrough: false, focusable: false);
            }
        });
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
            // Edit is the active/dwell state -> Accent (cyan). Move is a secondary state;
            // it must be visually distinct from Edit, so it takes the blue-leaning
            // SurfaceKeySwitch rather than the same Accent the migration had collapsed it to.
            NoteState.Edit => Tokens.BrushAccent,
            NoteState.Move => Tokens.BrushSurfaceKeySwitch,
            _ => Tokens.BrushSurfaceMedium
        };
    }

    private Thickness GetBorderThickness()
    {
        return _state == NoteState.Display ? new Thickness(1) : new Thickness(2);
    }

    private void DrawButtons()
    {
        // Move glyph takes SurfaceKeySwitch (blue) so it matches the Move state border and
        // does not collide with the Accent reserved for Edit/dwell-in-progress.
        var symbols = new[] { ("close", "✕", Tokens.AccentDanger),
                              ("edit",  "✎", Tokens.AccentSuccess),
                              ("move",  "↕", Tokens.SurfaceKeySwitch) };

        for (int i = 0; i < 3; i++)
        {
            var (suffix, symbol, color) = symbols[i];
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
                Text = symbol, FontSize = 14, Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(label, cx - 8);
            Canvas.SetTop(label, cy - 10);
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

    /// <summary>
    /// Shows or hides the control buttons to match the current state so the visible buttons stay
    /// in sync with the dwell targets registered by <c>NoteManager</c>:
    /// Display = close+edit+move, Edit = close only, Move = none (placement target only).
    /// </summary>
    private void UpdateButtonVisibility()
    {
        SetButtonVisible("close", _state != NoteState.Move);
        SetButtonVisible("edit", _state == NoteState.Display);
        SetButtonVisible("move", _state == NoteState.Display);
    }

    private void SetButtonVisible(string suffix, bool visible)
    {
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
