namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// The round-3 "eraseLeft" label frame (user's picks C3 + D3 "Aa", 2026-10-08): a ⌫-shaped
/// outline hugging the caption, drawn as geometry so the letters sit inside the arrow. The
/// arrow's stroke and tip slope are calibrated from the real ⌫ glyph's pixels (Segoe UI Symbol,
/// synthetic bold): stroke 0.08625 × the symbol font, tip 0.94791666… × the half height. The
/// frame's height follows the text's ink ∪ the cap band, so a short fill still gets a full-height
/// arrow.
/// </summary>
internal static class EraseLabelFrame
{
    /// <summary>The one <see cref="KeyDefinition.LabelFrame"/> value today: the erase arrow.</summary>
    public const string EraseLeft = "eraseLeft";

    /// <summary>Inner gap between the text ink and the stroke, as a fraction of the band.</summary>
    internal const double GapRatio = 0.32;

    /// <summary>Tip length, as a fraction of the frame's half height (measured from ⌫).</summary>
    internal const double TipRatio = 0.9479166666666666;

    /// <summary>Stroke thickness, as a fraction of the symbol font (measured from ⌫).</summary>
    internal const double StrokeRatio = 0.08625;

    /// <summary>The drawn frame: outer size in DIP, stroke thickness, chosen font, the inner gap as
    /// a fraction of the band, and the two geometries.</summary>
    internal sealed record Frame(
        double Width, double Height, double Stroke, double Font, double Gap, Geometry Arrow, Geometry Text);

    /// <summary>
    /// Wraps <paramref name="label"/> in the erase frame: the label becomes an invisible UIA
    /// anchor (AutomationId and Name intact) and a centred Canvas draws the arrow and the caption
    /// as geometry. Returns the holder the key's dwell outline wraps, so every repaint and the
    /// outline keep acting on the whole key.
    /// </summary>
    /// <param name="label">The key's caption TextBlock; its Text is the fill, its FontSize the key font.</param>
    /// <param name="keySize">The key Border's height in DIP.</param>
    /// <param name="boxWidth">The key Border's width in DIP.</param>
    /// <returns>A Grid holding the hidden label and the drawing.</returns>
    public static FrameworkElement Build(TextBlock label, double keySize, double boxWidth)
    {
        var frame = Measure(label.Text, label.FontSize, keySize, boxWidth);
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
            Data = frame.Arrow,
            Stroke = label.Foreground,
            StrokeThickness = frame.Stroke,
            StrokeLineJoin = PenLineJoin.Round
        });
        canvas.Children.Add(new Path { Data = frame.Text, Fill = label.Foreground });

        var holder = new Grid();
        holder.Children.Add(label);
        holder.Children.Add(canvas);
        return holder;
    }

    /// <summary>
    /// The frame's geometry for a caption at <paramref name="baseFont"/>: the text stays at the key
    /// font while it fits; if the frame would overrun the key minus 2 × EdgePadding, the text
    /// shrinks (never below <see cref="Legibility.MinimumGlyphFontSize"/>) and only at that floor
    /// does the inner gap give way.
    /// </summary>
    /// <param name="text">The caption drawn inside the arrow.</param>
    /// <param name="baseFont">The key font (<c>label.FontSize</c>).</param>
    /// <param name="keySize">The key Border's height in DIP.</param>
    /// <param name="boxWidth">The key Border's width in DIP.</param>
    /// <returns>The drawn frame.</returns>
    internal static Frame Measure(string text, double baseFont, double keySize, double boxWidth)
    {
        var room = boxWidth - 2 * SymbolCaptionRule.EdgePadding;
        var roomHeight = keySize - 2 * SymbolCaptionRule.EdgePadding;
        // The stroke matches the ⌫ key's own at this key size, so it does not shrink with the text.
        var stroke = StrokeRatio * baseFont * SymbolCaptionRule.Scale;

        bool Fits(double font, double gapRatio)
        {
            var f = Hug(text, font, stroke, gapRatio);
            return f.Width <= room + 1e-6 && f.Height <= roomHeight + 1e-6;
        }

        double drawingFont = baseFont, gap = GapRatio;
        if (!Fits(baseFont, GapRatio))
        {
            var floor = Legibility.MinimumGlyphFontSize;
            if (Fits(floor, GapRatio))
            {
                double lo = floor, hi = baseFont;
                for (var i = 0; i < 40; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (Fits(mid, GapRatio)) lo = mid; else hi = mid;
                }
                drawingFont = lo;
            }
            else
            {
                drawingFont = floor;
                double lo = 0, hi = GapRatio;
                for (var i = 0; i < 40; i++)
                {
                    var mid = (lo + hi) / 2;
                    if (Fits(floor, mid)) lo = mid; else hi = mid;
                }
                gap = lo;
            }
        }

        return Hug(text, drawingFont, stroke, gap);
    }

    /// <summary>The C3 frame: the text's ink ∪ cap band, an inner gap, then the stroke; the text
    /// slides as far into the tip as the gap allows, and the tip keeps ⌫'s slope.</summary>
    private static Frame Hug(string text, double font, double stroke, double gapRatio)
    {
        var textGeometry = TextGeometry(text, font);
        var ink = textGeometry.Bounds;
        var cap = TextGeometry("H", font).Bounds;
        var top = Math.Min(ink.Top, cap.Top);
        var bottom = Math.Max(ink.Bottom, cap.Bottom);
        var gap = gapRatio * (bottom - top);
        var height = (bottom - top) + 2 * (gap + stroke);
        var tip = TipRatio * height / 2;
        var textY = height / 2 - (top + bottom) / 2;

        var points = Flatten(textGeometry);
        var probe = Pentagon(10000, height, tip);
        double lo = 0, hi = 10000;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            var moved = points.Select(p => new Point(p.X + mid, p.Y + textY)).ToList();
            if (InsideBy(moved, probe, stroke + gap)) hi = mid; else lo = mid;
        }
        var textX = hi;
        var width = textX + ink.Right + gap + stroke;

        var placed = textGeometry.Clone();
        placed.Transform = new TranslateTransform(textX, textY);
        var outer = Pentagon(width, height, tip);
        return new Frame(width, height, stroke, font, gapRatio, Poly(Inset(outer, stroke / 2)), placed);
    }

    // ---- convex-polygon helpers (the designer's reference implementation) ----

    /// <summary>Outer pentagon (tip left), clockwise in screen coordinates.</summary>
    private static Point[] Pentagon(double w, double h, double tip) =>
        [new(0, h / 2), new(tip, 0), new(w, 0), new(w, h), new(tip, h)];

    /// <summary>Unit inward normals of a convex polygon's edges (edge i = p[i] → p[i+1]).</summary>
    private static Vector[] InwardNormals(Point[] p)
    {
        var area = 0.0;
        for (var i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        var n = new Vector[p.Length];
        for (var i = 0; i < p.Length; i++)
        {
            var e = p[(i + 1) % p.Length] - p[i];
            var v = area > 0 ? new Vector(-e.Y, e.X) : new Vector(e.Y, -e.X);
            v.Normalize();
            n[i] = v;
        }
        return n;
    }

    /// <summary>The convex polygon moved inward by d (each edge shifted, vertices re-intersected).</summary>
    private static Point[] Inset(Point[] p, double d)
    {
        var n = InwardNormals(p);
        var q = new Point[p.Length];
        for (var i = 0; i < p.Length; i++)
        {
            var j = (i - 1 + p.Length) % p.Length;            // previous edge
            Point a1 = p[j] + n[j] * d, a2 = p[i] + n[j] * d; // previous edge, shifted
            Point b1 = p[i] + n[i] * d, b2 = p[(i + 1) % p.Length] + n[i] * d;
            q[i] = Intersect(a1, a2, b1, b2);
        }
        return q;
    }

    private static Point Intersect(Point a1, Point a2, Point b1, Point b2)
    {
        var r = a2 - a1;
        var s = b2 - b1;
        var t = Vector.CrossProduct(b1 - a1, s) / Vector.CrossProduct(r, s);
        return a1 + r * t;
    }

    /// <summary>True when every point is at least <paramref name="margin"/> inside every edge of the convex polygon.</summary>
    private static bool InsideBy(IReadOnlyList<Point> pts, Point[] poly, double margin)
    {
        var n = InwardNormals(poly);
        foreach (var q in pts)
            for (var i = 0; i < poly.Length; i++)
                if (Vector.Multiply(q - poly[i], n[i]) < margin - 1e-9) return false;
        return true;
    }

    private static List<Point> Flatten(Geometry g)
    {
        var list = new List<Point>();
        foreach (var fig in g.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute).Figures)
        {
            list.Add(fig.StartPoint);
            foreach (var seg in fig.Segments)
                if (seg is LineSegment l) list.Add(l.Point);
                else if (seg is PolyLineSegment pl) list.AddRange(pl.Points);
        }
        return list;
    }

    private static Geometry Poly(Point[] p)
    {
        var fig = new PathFigure { StartPoint = p[0], IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(p.Skip(1), true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    private static Geometry TextGeometry(string text, double size) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Tokens.FontStandard, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, Tokens.BrushTextPrimary, 2.0)
            .BuildGeometry(new Point(0, 0));
}
