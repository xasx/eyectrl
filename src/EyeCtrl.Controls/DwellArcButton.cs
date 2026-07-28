namespace EyeCtrl.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;

public class DwellArcButton
{
    public const double DefaultArcInset = 4;
    public const double DefaultArcThickness = 4;
    public const double DefaultSymbolFontSize = 22;

    public Ellipse Background { get; }
    public Path ProgressPath { get; }
    public ArcSegment Arc { get; }
    public TextBlock? Symbol { get; }

    private readonly Point _center;
    private readonly double _arcRadius;

    private DwellArcButton(Ellipse background, Path progressPath, ArcSegment arc, TextBlock? symbol, Point center, double arcRadius)
    {
        Background = background;
        ProgressPath = progressPath;
        Arc = arc;
        Symbol = symbol;
        _center = center;
        _arcRadius = arcRadius;
    }

    public static DwellArcButton Create(Point center, double radius, string? symbol,
        Brush? fill = null, Brush? stroke = null, Brush? arcBrush = null, Brush? symbolBrush = null,
        double arcThickness = DefaultArcThickness, double arcInset = DefaultArcInset, double fontSize = DefaultSymbolFontSize)
    {
        var fillBrush = fill ?? Tokens.BrushSurfaceMedium;
        var strokeBrush = stroke ?? Tokens.BrushBorderSubtle;
        var arcBrushActual = arcBrush ?? Tokens.BrushAccent;
        var symbolBrushActual = symbolBrush ?? Brushes.White;
        var dia = radius * 2;
        var arcRad = radius - arcInset;

        var bg = new Ellipse
        {
            Width = dia,
            Height = dia,
            Fill = fillBrush,
            Stroke = strokeBrush,
            StrokeThickness = 1
        };
        Canvas.SetLeft(bg, center.X - radius);
        Canvas.SetTop(bg, center.Y - radius);

        var arcSegment = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRad),
            Size = new Size(arcRad, arcRad),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        var figure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRad),
            IsClosed = false
        };
        figure.Segments.Add(arcSegment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var progressPath = new Path
        {
            Data = geometry,
            Stroke = arcBrushActual,
            StrokeThickness = arcThickness
        };

        TextBlock? symbolText = null;
        if (symbol is not null)
        {
            symbolText = new TextBlock
            {
                Text = symbol,
                Foreground = symbolBrushActual,
                FontSize = fontSize,
                TextAlignment = TextAlignment.Center,
                Width = dia,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            symbolText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(symbolText, center.X - radius);
            Canvas.SetTop(symbolText, center.Y - symbolText.DesiredSize.Height / 2);
        }

        return new DwellArcButton(bg, progressPath, arcSegment, symbolText, center, arcRad);
    }

    public void UpdateProgress(double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped <= 0)
        {
            ProgressPath.Visibility = Visibility.Collapsed;
            return;
        }
        ProgressPath.Visibility = Visibility.Visible;

        var angle = clamped * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;

        var endX = _center.X + _arcRadius * Math.Sin(radians);
        var endY = _center.Y - _arcRadius * Math.Cos(radians);

        Arc.Point = new Point(endX, endY);
        Arc.IsLargeArc = angle > 180;
    }

    public static void UpdateArc(ArcSegment arc, Point center, double arcRadius, double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var angle = clamped * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;

        var endX = center.X + arcRadius * Math.Sin(radians);
        var endY = center.Y - arcRadius * Math.Cos(radians);

        arc.Point = new Point(endX, endY);
        arc.IsLargeArc = angle > 180;
    }

    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(Background);
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
    }
}
