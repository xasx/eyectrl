namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;

public partial class ZoomSideButtonWindow : OverlayWindow
{
    private const double ButtonRadius = 50;

    private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc, Point CanvasCenter)> _buttonVisuals = new();

    public ZoomSideButtonWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void ShowAt(ScreenPoint closeCenter, ScreenPoint recaptureCenter)
    {
        _buttonVisuals.Clear();
        ButtonCanvas.Children.Clear();

        // Compute bounding box around both button centers with padding
        var minX = Math.Min(closeCenter.X, recaptureCenter.X) - ButtonRadius - 10;
        var minY = Math.Min(closeCenter.Y, recaptureCenter.Y) - ButtonRadius - 10;
        var maxX = Math.Max(closeCenter.X, recaptureCenter.X) + ButtonRadius + 10;
        var maxY = Math.Max(closeCenter.Y, recaptureCenter.Y) + ButtonRadius + 10;

        Width = maxX - minX;
        Height = maxY - minY;
        Left = minX;
        Top = minY;

        // Draw close button (top)
        DrawButton("zoom_close", "\u2715", closeCenter.X - minX, closeCenter.Y - minY);

        // Draw recapture button (bottom)
        DrawButton("zoom_recapture", "\u21BB", recaptureCenter.X - minX, recaptureCenter.Y - minY);

        Show();
    }

    private void DrawButton(string id, string symbol, double cx, double cy)
    {
        // Background circle
        var bg = new Ellipse
        {
            Width = ButtonRadius * 2,
            Height = ButtonRadius * 2,
            Fill = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
            Stroke = new SolidColorBrush(Color.FromArgb(100, 100, 100, 100)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(bg, cx - ButtonRadius);
        Canvas.SetTop(bg, cy - ButtonRadius);
        ButtonCanvas.Children.Add(bg);

        // Progress arc (dwell indicator ring)
        var arcRadius = ButtonRadius - 4;
        var arcSegment = new ArcSegment
        {
            Point = new Point(cx, cy - arcRadius),
            Size = new Size(arcRadius, arcRadius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        var figure = new PathFigure
        {
            StartPoint = new Point(cx, cy - arcRadius),
            IsClosed = false
        };
        figure.Segments.Add(arcSegment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var progressPath = new Path
        {
            Data = geometry,
            Stroke = Brushes.Lime,
            StrokeThickness = 4
        };
        ButtonCanvas.Children.Add(progressPath);

        _buttonVisuals[id] = (bg, progressPath, arcSegment, new Point(cx, cy));

        // Symbol label
        var textBlock = new TextBlock
        {
            Text = symbol,
            Foreground = Brushes.White,
            FontSize = 22,
            TextAlignment = TextAlignment.Center,
            Width = ButtonRadius * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(textBlock, cx - ButtonRadius);
        Canvas.SetTop(textBlock, cy - textBlock.DesiredSize.Height / 2);
        ButtonCanvas.Children.Add(textBlock);
    }

    public void UpdateProgress(string buttonId, double progress)
    {
        if (!_buttonVisuals.TryGetValue(buttonId, out var visuals)) return;

        var (_, _, arcSegment, center) = visuals;
        var radius = ButtonRadius - 4;

        var angle = progress * 360;
        var radians = angle * Math.PI / 180;

        var endX = center.X + radius * Math.Sin(radians);
        var endY = center.Y - radius * Math.Cos(radians);

        arcSegment.Point = new Point(endX, endY);
        arcSegment.IsLargeArc = angle > 180;
    }
}
