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

    /// <summary>
    /// Diameter below which a glyph needs <see cref="ContainerRatio.Badge"/>'s larger fraction
    /// to stay legible. The 40px edge-bar drag buttons (<c>Drag_cancel</c>, <c>Drag_back</c>,
    /// which take <c>EdgeButtonDefinition</c>'s default <c>UIConstants.DragButtonRadius</c> of
    /// 20) would render at 40 × 0.23 = 9.2px under <see cref="ContainerRatio.Small"/> — below
    /// the 12px accessibility floor, on the affordance that cancels a drag for a user whose
    /// only input channel is an eye tracker. Above the threshold the 100px callers
    /// (<c>ActionMenuGeometry.ButtonRadius</c> / <c>UIConstants.ButtonRadius</c> = 50) keep
    /// Small, which is the ratio they already observe (22/100 = 0.22).
    ///
    /// Named rather than written inline so RawGeometryLiteralTests's bare-number rule does not
    /// flag the <c>FontSize</c> expression that reads it.
    ///
    /// Both arms are asserted by <c>DwellArcButtonGlyphSizeTests</c>. They have to be: the
    /// expression is token-backed whichever ratio each arm picks, so that guard passes if the arms
    /// are swapped or the branch collapses onto one ratio.
    /// </summary>
    private const double BadgeDiameterThreshold = 64;

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

    // `fontSize` is nullable rather than defaulted because a defaulted parameter cannot
    // reference `radius`; the size is resolved from the diameter at the assignment site below.
    // The old `DefaultSymbolFontSize = 22` is gone — 22 was 0.22 of the 100px diameter the
    // action-menu and hide-button callers use, i.e. ContainerRatio.Small.
    //
    // It is also an unguarded hole in RawGeometryLiteralTests, recorded here rather than left to
    // be rediscovered: that guard matches `FontSize = <value>` assignments, so a caller writing
    // `fontSize: 22` passes a raw size in through an ARGUMENT and is never seen. Accepted, not
    // overlooked — all three callers (ActionMenuWindow, EdgeButtonBarWindow, GlassBackdropWindow)
    // omit the parameter entirely and take the ratio, which is the point of it defaulting to null.
    // If a caller ever does pass it, pass a ContainerRatio expression, and widen the guard to
    // named arguments rather than treating this comment as permission.
    public static DwellArcButton Create(Point center, double radius, string? symbol,
        Brush? fill = null, Brush? stroke = null, Brush? arcBrush = null, Brush? symbolBrush = null,
        double arcThickness = DefaultArcThickness, double arcInset = DefaultArcInset,
        double? fontSize = null)
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
                // Sized from the diameter that bounds it, not from a constant. Assigned inline
                // rather than hoisted into a local: the guard examines the assignment's own
                // value, and a local name references no token.
                FontSize = fontSize ?? dia * (dia < BadgeDiameterThreshold ? ContainerRatio.Badge : ContainerRatio.Small),
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
