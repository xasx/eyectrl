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
    public Path HaloPath { get; }
    public Path ProgressPath { get; }
    public ArcSegment HaloArc { get; }
    public ArcSegment Arc { get; }
    public TextBlock? Symbol { get; }

    private readonly Point _center;
    private readonly double _arcRadius;

    private DwellArcButton(Ellipse background, Path haloPath, Path progressPath, ArcSegment haloArc, ArcSegment arc, TextBlock? symbol, Point center, double arcRadius)
    {
        Background = background;
        HaloPath = haloPath;
        ProgressPath = progressPath;
        HaloArc = haloArc;
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
        var symbolBrushActual = symbolBrush ?? Tokens.BrushTextPrimary;
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

        // Halo under-stroke (spec §4.1.3). ActionMenu and EdgeButtonBar are both
        // transparent overlays, so this is where their arcs get a contrast
        // guarantee - neither window file needs to change.
        var haloSegment = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRad),
            Size = new Size(arcRad, arcRad),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
        };
        var haloFigure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRad),
            IsClosed = false,
        };
        haloFigure.Segments.Add(haloSegment);
        var haloGeometry = new PathGeometry();
        haloGeometry.Figures.Add(haloFigure);
        var haloPath = new Path
        {
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = arcThickness + 2,
            Data = haloGeometry,
        };

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

        return new DwellArcButton(bg, haloPath, progressPath, haloSegment, arcSegment, symbolText, center, arcRad);
    }

    public void UpdateProgress(double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped <= 0)
        {
            ProgressPath.Visibility = Visibility.Collapsed;
            HaloPath.Visibility = Visibility.Collapsed;
            return;
        }
        ProgressPath.Visibility = Visibility.Visible;
        HaloPath.Visibility = Visibility.Visible;

        UpdateArc(Arc, _center, _arcRadius, progress);
        UpdateArc(HaloArc, _center, _arcRadius, progress);
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
        canvas.Children.Add(HaloPath);      // must precede ProgressPath
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
    }
}
