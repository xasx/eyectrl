namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;

public class NoteWindow : OverlayWindow
{
    private const double ButtonRadius = LayoutConstants.NoteButtonRadius;
    private const double ButtonDiameter = ButtonRadius * 2;
    private const double ButtonGap = 10;
    private const double ButtonPadding = 10;
    private const double TextAreaWidth = 200;

    private readonly string _noteId;
    private readonly Canvas _canvas;
    private readonly Dictionary<string, Ellipse> _buttons = new();
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
        Height = 140;

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
        _arcs.Clear();

        var bg = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            CornerRadius = new CornerRadius(6),
            BorderBrush = GetBorderBrush(),
            BorderThickness = GetBorderThickness(),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        _canvas.Children.Add(bg);

        var titleBar = new Border
        {
            Height = 24, Width = LayoutConstants.NoteWindowWidth,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x3F)),
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };
        var titleLabel = new TextBlock
        {
            Text = "PINNED NOTE", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(8, 4, 0, 0)
        };
        titleBar.Child = titleLabel;
        Canvas.SetLeft(titleBar, 0); Canvas.SetTop(titleBar, 0);
        _canvas.Children.Add(titleBar);

        _contentBorder = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height - 24,
            Background = Brushes.Transparent
        };
        Canvas.SetLeft(_contentBorder, 0); Canvas.SetTop(_contentBorder, 24);
        _canvas.Children.Add(_contentBorder);

        DrawButtons();
        UpdateContent();
    }

    public void UpdateContent()
    {
        if (_contentBorder == null) return;
        _contentBorder.Child = null;

        if (_state == NoteState.Edit)
        {
            _textBox = new TextBox
            {
                Width = TextAreaWidth - 20, Height = Height - 44,
                Margin = new Thickness(10, 10, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x40)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Colors.Lime),
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
            _textBlock = new TextBlock
            {
                Width = TextAreaWidth - 20,
                MaxWidth = TextAreaWidth - 20,
                Margin = new Thickness(10, 10, 0, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xEA, 0xA7)),
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

    public void SetState(NoteState state)
    {
        _state = state;
        Dispatcher.Invoke(() =>
        {
            UpdateContent();
            UpdateBorder();

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
            NoteState.Edit => new SolidColorBrush(Colors.Lime),
            NoteState.Move => new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
            _ => new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x44))
        };
    }

    private Thickness GetBorderThickness()
    {
        return _state == NoteState.Display ? new Thickness(1) : new Thickness(2);
    }

    private void DrawButtons()
    {
        var symbols = new[] { ("close", "\u2715", Color.FromRgb(0xFF, 0x66, 0x66)),
                              ("edit",  "\u270E", Color.FromRgb(0x66, 0xFF, 0x66)),
                              ("move",  "\u2195", Color.FromRgb(0x66, 0x88, 0xFF)) };

        for (int i = 0; i < 3; i++)
        {
            var (suffix, symbol, color) = symbols[i];
            double cx = LayoutConstants.NoteWindowWidth - ButtonPadding - ButtonRadius;
            double cy = ButtonPadding + ButtonRadius + i * (ButtonDiameter + ButtonGap);

            var ellipse = new Ellipse
            {
                Width = ButtonDiameter, Height = ButtonDiameter,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
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

            var arcPath = new Path
            {
                Stroke = new SolidColorBrush(Colors.Lime),
                StrokeThickness = 3, Visibility = Visibility.Hidden
            };
            Canvas.SetLeft(arcPath, 0); Canvas.SetTop(arcPath, 0);
            _canvas.Children.Add(arcPath);
            _arcs[$"{_noteId}:{suffix}"] = (arcPath, -90);
        }
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
            double sweep = progress * 360;
            var fig = new PathFigure { StartPoint = new Point(cx + r * Math.Cos(startAngle * Math.PI / 180), cy + r * Math.Sin(startAngle * Math.PI / 180)) };
            var seg = new ArcSegment
            {
                Point = new Point(cx + r * Math.Cos((startAngle + sweep) * Math.PI / 180), cy + r * Math.Sin((startAngle + sweep) * Math.PI / 180)),
                Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = sweep > 180
            };
            arcPath.Data = new PathGeometry(new[] { fig });
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Loaded -= OnLoaded;
    }
}
