namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ManualSelectionSideButtonWindow : OverlayWindow
{
    // UI Layout Constants
    private const double ButtonRadius = 50; // Radius of ManualSelection side buttons in pixels
    private const int ButtonMargin = 10; // Margin around buttons for window sizing
    private const int ProgressArcInset = 4; // Inset of progress arc from button edge
    private const int ButtonSymbolFontSize = 22; // Font size for button symbols
    private const int ProgressArcThickness = 4; // Thickness of dwell progress arc
    private const byte ButtonBackgroundAlpha = 200; // Alpha value for button background
    private const byte ButtonBorderAlpha = 100; // Alpha value for button border

    private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc, Point CanvasCenter)> _buttonVisuals = new();

    public ManualSelectionSideButtonWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void ShowAt(PhysicalPoint closeCenter, PhysicalPoint recaptureCenter)
    {
        _buttonVisuals.Clear();
        ButtonCanvas.Children.Clear();

        // Convert physical → DIP at the boundary
        var closeDipX = DpiHelper.PhysicalToLogicalX(closeCenter.X);
        var closeDipY = DpiHelper.PhysicalToLogicalY(closeCenter.Y);
        var recapDipX = DpiHelper.PhysicalToLogicalX(recaptureCenter.X);
        var recapDipY = DpiHelper.PhysicalToLogicalY(recaptureCenter.Y);

        var minX = Math.Min(closeDipX, recapDipX) - ButtonRadius - ButtonMargin;
        var minY = Math.Min(closeDipY, recapDipY) - ButtonRadius - ButtonMargin;
        var maxX = Math.Max(closeDipX, recapDipX) + ButtonRadius + ButtonMargin;
        var maxY = Math.Max(closeDipY, recapDipY) + ButtonRadius + ButtonMargin;

        Width = maxX - minX;
        Height = maxY - minY;
        Left = minX;
        Top = minY;

        DrawButton("ManualSelection_close", "\u2715", closeDipX - minX, closeDipY - minY);
        DrawButton("ManualSelection_recapture", "\u21BB", recapDipX - minX, recapDipY - minY);

        Show();
    }

    private void DrawButton(string id, string symbol, double cx, double cy)
    {
        // Background circle
        var bg = new Ellipse
        {
            Width = ButtonRadius * 2,
            Height = ButtonRadius * 2,
            Fill = new SolidColorBrush(Color.FromArgb(ButtonBackgroundAlpha, 30, 30, 30)),
            Stroke = new SolidColorBrush(Color.FromArgb(ButtonBorderAlpha, 100, 100, 100)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(bg, cx - ButtonRadius);
        Canvas.SetTop(bg, cy - ButtonRadius);
        ButtonCanvas.Children.Add(bg);

        // Progress arc (dwell indicator ring)
        var arcRadius = ButtonRadius - ProgressArcInset;
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
            StrokeThickness = ProgressArcThickness
        };
        ButtonCanvas.Children.Add(progressPath);

        _buttonVisuals[id] = (bg, progressPath, arcSegment, new Point(cx, cy));

        // Symbol label
        var textBlock = new TextBlock
        {
            Text = symbol,
            Foreground = Brushes.White,
            FontSize = ButtonSymbolFontSize,
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
        var radius = ButtonRadius - ProgressArcInset;

        var angle = progress * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;

        var endX = center.X + radius * Math.Sin(radians);
        var endY = center.Y - radius * Math.Cos(radians);

        arcSegment.Point = new Point(endX, endY);
        arcSegment.IsLargeArc = angle > 180;
    }
}
