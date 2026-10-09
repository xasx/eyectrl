namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// The round-6 wide arrows (user's pick G4+, 2026-10-09): Caps, Shift and Enter draw an arrow
/// about 1/3 of a 1.2-unit key's box wide, so the glyphs stop being the narrowest marks on the
/// page. Modelled on <see cref="EraseLabelFrame"/>: the label stays the automation anchor at
/// Opacity 0, the arrow is geometry in a centred Canvas, and the stroke is the ⌫ key's own
/// (the symbol font's measured 0.08625). Caps is filled and carries the ⇪ lock bar; Shift is an
/// outline; Enter is a stem with a filled head.
/// </summary>
internal static class ArrowLabelFrame
{
    /// <summary>Caps: a filled arrow with the ⇪ lock bar under it.</summary>
    public const string CapsArrow = "capsArrow";
    /// <summary>Shift: the same arrow, outline only.</summary>
    public const string ShiftArrow = "shiftArrow";
    /// <summary>Enter: a stem down the right with a filled head pointing left.</summary>
    public const string EnterArrow = "enterArrow";
    /// <summary>Tab: a shaft into a filled head pointing right, stopped by a full-height bar.</summary>
    public const string TabArrow = "tabArrow";

    /// <summary>The arrow's height, as a multiple of its ink width.</summary>
    internal const double Aspect = 1.1;
    /// <summary>How much of the arrow's inset height the head takes.</summary>
    internal const double HeadRatio = 0.55;
    /// <summary>How much of the arrow's inset width the shaft takes.</summary>
    internal const double ShaftRatio = 0.48;
    /// <summary>The Caps bar's height, as a multiple of the ink width.</summary>
    internal const double BarRatio = 0.16;
    /// <summary>The gap between the Caps arrow and its bar, as a multiple of the ink width.</summary>
    internal const double BarGapRatio = 0.10;

    /// <summary>Enter's height, as a multiple of the ink width.</summary>
    internal const double EnterAspect = 0.75;
    /// <summary>Enter's head length, as a multiple of the ink width.</summary>
    internal const double EnterHeadLength = 0.32;
    /// <summary>Enter's head half-height, as a multiple of the ink width.</summary>
    internal const double EnterHeadHalf = 0.24;

    /// <summary>Tab's height, as a multiple of the ink width.</summary>
    internal const double TabAspect = 0.66;

    /// <summary>The drawn arrow: ink size in DIP, stroke thickness, and the geometry.</summary>
    internal sealed record Frame(double Width, double Height, double Stroke, bool Filled, Geometry Path);

    /// <summary>True when <paramref name="labelFrame"/> names one of the four drawn arrows.</summary>
    public static bool IsArrow(string? labelFrame) =>
        labelFrame is CapsArrow or ShiftArrow or EnterArrow or TabArrow;

    /// <summary>
    /// The user's target ink width: one third of a 1.2-unit key's box at this key size. The same
    /// width for Enter, whose key is wider.
    /// </summary>
    /// <param name="keySize">The key size in DIP.</param>
    /// <returns>The ink width in DIP.</returns>
    internal static double TargetWidth(double keySize) => (keySize * 1.2 + 0.2 * 4) / 3;

    /// <summary>
    /// Wraps <paramref name="label"/> in the drawn arrow: the label becomes the invisible
    /// automation anchor, and a centred Canvas draws the frame geometry in the label's own
    /// foreground. Returns the holder the key's dwell outline wraps.
    /// </summary>
    /// <param name="frameId">One of <see cref="CapsArrow"/>, <see cref="ShiftArrow"/>, <see cref="EnterArrow"/>.</param>
    /// <param name="label">The key's caption TextBlock; its FontSize is the symbol font (base × Scale).</param>
    /// <param name="keySize">The key Border's height (and the keyboard's key size) in DIP.</param>
    /// <returns>A Grid holding the hidden label and the drawing.</returns>
    public static FrameworkElement Build(string frameId, TextBlock label, double keySize)
    {
        var frame = Measure(frameId, label.FontSize, keySize);
        label.Opacity = 0;

        var canvas = new Canvas
        {
            Width = frame.Width,
            Height = frame.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        canvas.Children.Add(new Path
        {
            Data = frame.Path,
            Stroke = label.Foreground,
            StrokeThickness = frame.Stroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = frame.Filled ? label.Foreground : null
        });

        var holder = new Grid();
        holder.Children.Add(label);
        holder.Children.Add(canvas);
        return holder;
    }

    /// <summary>
    /// The arrow's geometry at one key size. The stroke is the symbol font's (⌫'s own), taken from
    /// the label's font so it matches the ⌫ key beside it; the ink width is exactly the target, so
    /// the frame needs no fit limit — it fits every allowed size with room to spare.
    /// </summary>
    /// <param name="frameId">Which arrow to draw.</param>
    /// <param name="symbolFont">The label's font size (base × <see cref="SymbolCaptionRule.Scale"/>).</param>
    /// <param name="keySize">The key size in DIP.</param>
    /// <returns>The drawn frame.</returns>
    internal static Frame Measure(string frameId, double symbolFont, double keySize)
    {
        var stroke = EraseLabelFrame.StrokeRatio * symbolFont;
        var width = TargetWidth(keySize);

        if (frameId == EnterArrow)
        {
            var height = width * EnterAspect;
            var headLength = EnterHeadLength * width;
            var headHalf = EnterHeadHalf * width;
            var centreY = height - headHalf;
            var o = stroke / 2;
            var line = new PathFigure { StartPoint = new Point(width - o, o), IsClosed = false, IsFilled = false };
            line.Segments.Add(new PolyLineSegment([new Point(width - o, centreY), new Point(headLength, centreY)], true));
            var head = new PathFigure { StartPoint = new Point(o, centreY), IsClosed = true, IsFilled = true };
            head.Segments.Add(new PolyLineSegment(
                [new Point(headLength, centreY - headHalf + o), new Point(headLength, centreY + headHalf - o)], true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(line);
            geometry.Figures.Add(head);
            return new Frame(width, height, stroke, Filled: true, geometry);
        }

        if (frameId == TabArrow)
        {
            // ⇥ as →|: the shaft runs into Enter's head, one stroke of air before the stop bar.
            var height = width * TabAspect;
            var headLength = EnterHeadLength * width;
            var headHalf = EnterHeadHalf * width;
            var centreY = height / 2;
            var o = stroke / 2;
            var tipX = width - 2 * stroke - o;   // tip ink at w − 2t: one stroke clear of the bar's ink
            var geometry = new PathGeometry();
            geometry.Figures.Add(LineFigure(new Point(o, centreY), new Point(tipX - headLength, centreY)));
            geometry.Figures.Add(HeadFigure(new Point(tipX, centreY), tipX - headLength, centreY, headHalf, o));
            geometry.Figures.Add(LineFigure(new Point(width - o, o), new Point(width - o, height - o)));
            return new Frame(width, height, stroke, Filled: true, geometry);
        }

        var filledFrame = frameId == CapsArrow;
        var arrowHeight = width * Aspect;
        var group = new GeometryGroup();
        group.Children.Add(ArrowPoly(width, arrowHeight, stroke));
        var totalHeight = arrowHeight;
        if (filledFrame)
        {
            // The ⇪ lock bar: as wide as the shaft's ink, 0.10w below the arrow.
            var shaftInk = ShaftRatio * (width - stroke) + stroke;
            var barY = arrowHeight + BarGapRatio * width;
            var barHeight = BarRatio * width;
            group.Children.Add(new RectangleGeometry(new Rect(
                (width - shaftInk) / 2 + stroke / 2, barY + stroke / 2,
                shaftInk - stroke, barHeight - stroke)));
            totalHeight = barY + barHeight;
        }
        return new Frame(width, totalHeight, stroke, filledFrame, group);
    }

    /// <summary>An unfilled straight line figure, drawn with the frame's round caps.</summary>
    private static PathFigure LineFigure(Point from, Point to)
    {
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment(to, true));
        return figure;
    }

    /// <summary>A filled triangular head: <paramref name="tip"/> to a base at
    /// <paramref name="baseX"/>, its corners inset by <paramref name="o"/> so the stroke's ink
    /// lands on the nominal triangle.</summary>
    private static PathFigure HeadFigure(Point tip, double baseX, double centreY, double half, double o)
    {
        var figure = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(
            [new Point(baseX, centreY - half + o), new Point(baseX, centreY + half - o)], true));
        return figure;
    }

    /// <summary>The seven-point up-arrow outline, inset by half the stroke so the round-joined
    /// stroke's ink lands exactly on the w × h box.</summary>
    private static Geometry ArrowPoly(double w, double h, double t)
    {
        double innerW = w - t, innerH = h - t, o = t / 2;
        var head = HeadRatio * innerH;
        var shaft = ShaftRatio * innerW;
        Point P(double x, double y) => new(o + x, o + y);
        Point[] points =
        [
            P(innerW / 2, 0), P(innerW, head), P(innerW / 2 + shaft / 2, head), P(innerW / 2 + shaft / 2, innerH),
            P(innerW / 2 - shaft / 2, innerH), P(innerW / 2 - shaft / 2, head), P(0, head),
        ];
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
