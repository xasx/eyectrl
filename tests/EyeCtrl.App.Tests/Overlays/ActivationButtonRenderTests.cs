namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// B2/F1's activation button face, rendered headlessly at the user's 2.0 (and at 1.0 where the
/// ticket asks for it). Nothing here shows a window: the production <see cref="ActivationButtonFace"/>
/// is arranged in a plain host and drawn through a <see cref="RenderTargetBitmap"/>; the probes read
/// rendered pixels, never brush declarations alone.
///
/// <para>
/// Contrast is read on the BAND BODY: ink pixels are matched to the colour the copy's brush actually
/// renders to over the local background (a 98 %-opaque face brush composites over the page; an
/// antialiased glyph core does not), and the median of those body pixels is the figure — never the
/// single best pixel.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ActivationButtonRenderTests
{
    private const double Scale = 2.0; // the owner's 3840 x 2400 at 200 %
    private const double Tolerance = 24; // summed |dR|+|dG|+|dB| for a body pixel

    private readonly ITestOutputHelper _output;

    public ActivationButtonRenderTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> Themes()
    {
        var data = new TheoryData<string>();
        foreach (var name in ThemeRegistry.Names) data.Add(name);
        return data;
    }

    /// <summary>One headless frame: the production face laid out over an opaque page.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly Grid _host;
        private readonly double _scale;
        private readonly double _hostDip;
        private readonly double _offset;

        public ActivationButtonFace Face { get; }

        public Scene(
            Color page,
            double progress,
            bool paused = false,
            double hostDip = 100,
            double scale = Scale)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            _scale = scale;
            _hostDip = hostDip;
            _offset = (hostDip - ActivationButtonFace.Diameter) / 2;

            Face = new ActivationButtonFace();
            Face.UpdateProgress(progress);
            Face.SetPaused(paused);

            _host = new Grid
            {
                Width = hostDip,
                Height = hostDip,
                Background = new SolidColorBrush(page),
            };
            _host.Children.Add(Face);
            _host.Measure(new Size(hostDip, hostDip));
            _host.Arrange(new Rect(0, 0, hostDip, hostDip));
            _host.UpdateLayout();
        }

        public Probe Render()
        {
            var bitmap = RenderBitmap();
            int pixels = bitmap.PixelWidth;
            var stride = pixels * 4;
            var data = new byte[stride * pixels];
            bitmap.CopyPixels(data, stride, 0);
            return new Probe(data, pixels, _scale, _offset);
        }

        /// <summary>The same laid-out frame as a bitmap, for the review PNG writer.</summary>
        public RenderTargetBitmap RenderBitmap()
        {
            // The clips/height change AFTER the constructor's layout pass; RenderTargetBitmap does
            // not lay out, so run the pass here or the old geometry renders.
            _host.Measure(new Size(_hostDip, _hostDip));
            _host.Arrange(new Rect(0, 0, _hostDip, _hostDip));
            _host.UpdateLayout();

            int pixels = (int)Math.Round(_hostDip * _scale);
            var bitmap = new RenderTargetBitmap(
                pixels, pixels, 96.0 * _scale, 96.0 * _scale, PixelFormats.Pbgra32);
            bitmap.Render(_host);
            return bitmap;
        }

        public void Dispose() => _host.Children.Clear();
    }

    /// <summary>A rendered frame in device pixels, queried in the face's DIP coordinates.</summary>
    private sealed class Probe
    {
        private readonly byte[] _pixels;
        private readonly int _width;
        private readonly double _scale;
        private readonly double _offset;

        public Probe(byte[] pixels, int width, double scale, double offset)
        {
            _pixels = pixels;
            _width = width;
            _scale = scale;
            _offset = offset;
        }

        public Color At(double xDip, double yDip)
        {
            var x = Math.Clamp((int)Math.Round((xDip + _offset) * _scale), 0, _width - 1);
            var y = Math.Clamp((int)Math.Round((yDip + _offset) * _scale), 0, _width - 1);
            var i = ((y * _width) + x) * 4;
            return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }

        /// <summary>The painted horizontal span (DIP) along the face's centre row.</summary>
        public double PaintedSpanDip(Color page, double yDip)
        {
            int first = int.MaxValue;
            int last = int.MinValue;
            for (var x = 0; x < _width; x++)
            {
                var i = (((int)Math.Round((yDip + _offset) * _scale) * _width) + x) * 4;
                var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                if (Distance(pixel, page) <= Tolerance) continue;
                first = Math.Min(first, x);
                last = Math.Max(last, x);
            }

            return last < first ? 0 : (last - first + 1) / _scale;
        }

        /// <summary>
        /// The ink's bounding box in DIP: body pixels whose rendered colour is within the body
        /// tolerance of <paramref name="brush"/> composited over <paramref name="background"/>.
        /// </summary>
        public Rect InkBoundsDip(Brush brush, Color background, Rect areaDip)
        {
            var expected = ExpectedColour(brush, background);
            int x0 = (int)Math.Ceiling((areaDip.Left + _offset) * _scale);
            int x1 = (int)Math.Floor((areaDip.Right + _offset) * _scale);
            int y0 = (int)Math.Ceiling((areaDip.Top + _offset) * _scale);
            int y1 = (int)Math.Floor((areaDip.Bottom + _offset) * _scale);
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            var count = 0;
            for (int y = Math.Max(0, y0); y <= Math.Min(_width - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(_width - 1, x1); x++)
                {
                    var i = ((y * _width) + x) * 4;
                    var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                    if (Distance(pixel, expected) > Tolerance) continue;
                    count++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }

            Check.WithCustomMessage(
                    $"the sign's ink is not the expected colour {expected} over {background} "
                    + $"({count} body pixels found); the copy or the clip is wrong.")
                .That(count).IsStrictlyGreaterThan(12);

            return new Rect(
                (minX / _scale) - _offset,
                (minY / _scale) - _offset,
                ((maxX - minX) + 1) / _scale,
                ((maxY - minY) + 1) / _scale);
        }

        /// <summary>The largest distance (DIP) of any ink body pixel in the area from the centre.</summary>
        public double MaxInkRadiusDip(Brush brush, Color background, Rect areaDip)
        {
            var expected = ExpectedColour(brush, background);
            int x0 = (int)Math.Ceiling((areaDip.Left + _offset) * _scale);
            int x1 = (int)Math.Floor((areaDip.Right + _offset) * _scale);
            int y0 = (int)Math.Ceiling((areaDip.Top + _offset) * _scale);
            int y1 = (int)Math.Floor((areaDip.Bottom + _offset) * _scale);
            double max = 0;
            for (int y = Math.Max(0, y0); y <= Math.Min(_width - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(_width - 1, x1); x++)
                {
                    var i = ((y * _width) + x) * 4;
                    var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                    if (Distance(pixel, expected) > Tolerance) continue;
                    double dx = ((x / _scale) - _offset) - (ActivationButtonFace.Diameter / 2);
                    double dy = ((y / _scale) - _offset) - (ActivationButtonFace.Diameter / 2);
                    max = Math.Max(max, Math.Sqrt((dx * dx) + (dy * dy)));
                }

            return max;
        }

        /// <summary>
        /// The number of distinct painted arcs on a circle of <paramref name="radiusDip"/> around
        /// the disc's centre — the six-circles-with-gaps probe.
        /// </summary>
        public int InkRunsOnCircle(Color background, double radiusDip, int samples = 4096)
        {
            int runs = 0;
            bool previous = false;
            double cx = ActivationButtonFace.Diameter / 2;
            double cy = ActivationButtonFace.Diameter / 2;
            for (int i = 0; i < samples; i++)
            {
                double angle = 2 * Math.PI * i / samples;
                var pixel = At(cx + (radiusDip * Math.Cos(angle)), cy + (radiusDip * Math.Sin(angle)));
                bool ink = Distance(pixel, background) > Tolerance;
                if (ink && !previous) runs++;
                previous = ink;
            }

            return runs;
        }

        /// <summary>
        /// The median contrast of the ink BODY inside <paramref name="rectDip"/>: pixels whose
        /// rendered colour is within a small tolerance of the copy's brush composited over the local
        /// background. The median, and a minimum count, is what keeps an antialias fringe from
        /// vouching for a body that is unreadable.
        /// </summary>
        public double InkContrast(Rect rectDip, Brush brush, Color background)
        {
            var expected = ExpectedColour(brush, background);
            var contrasts = new List<double>();
            int x0 = (int)Math.Ceiling((rectDip.Left + _offset) * _scale);
            int x1 = (int)Math.Floor((rectDip.Right + _offset) * _scale);
            int y0 = (int)Math.Ceiling((rectDip.Top + _offset) * _scale);
            int y1 = (int)Math.Floor((rectDip.Bottom + _offset) * _scale);
            for (int y = Math.Max(0, y0); y <= Math.Min(_width - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(_width - 1, x1); x++)
                {
                    var i = ((y * _width) + x) * 4;
                    var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                    if (Distance(pixel, expected) <= Tolerance)
                        contrasts.Add(Contrast(pixel, background));
                }

            Check.WithCustomMessage(
                    $"the ink in {rectDip} is not the expected colour {expected} over {background} "
                    + $"({contrasts.Count} body pixels found); the clip or the copy is wrong.")
                .That(contrasts.Count).IsStrictlyGreaterThan(12);
            contrasts.Sort();
            return contrasts[contrasts.Count / 2];
        }
    }

    private static double Luminance(Color c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Blend(Color foreground, Color background, double alpha) => Color.FromRgb(
        (byte)Math.Round((foreground.R * alpha) + (background.R * (1 - alpha))),
        (byte)Math.Round((foreground.G * alpha) + (background.G * (1 - alpha))),
        (byte)Math.Round((foreground.B * alpha) + (background.B * (1 - alpha))));

    private static Color ExpectedColour(Brush brush, Color background)
    {
        if (brush is not SolidColorBrush solid)
            throw new ArgumentException($"expected a SolidColorBrush, found {brush.GetType()}", nameof(brush));
        var colour = solid.Color;
        return Blend(Color.FromRgb(colour.R, colour.G, colour.B), background, colour.A / 255.0);
    }

    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    /// <summary>
    /// The rendered ring radius (DIP), measured along the vertical ray through the 12 o'clock
    /// circle: that circle's ink spans from <c>R - r - s/2</c> to <c>R + r + s/2</c>, so the
    /// midpoint of the first and last ink pixel is the ring radius. The scan starts outside the
    /// centre dot and stops before anything else.
    /// </summary>
    private static double RingRadiusDip(Probe probe, Color face)
    {
        double first = double.NaN;
        double last = double.NaN;
        for (double radius = 8.0; radius <= 34.0; radius += 0.25)
        {
            if (Distance(probe.At(ActivationButtonFace.Diameter / 2, (ActivationButtonFace.Diameter / 2) - radius), face) <= Tolerance)
                continue;
            if (double.IsNaN(first)) first = radius;
            last = radius;
        }

        Check.WithCustomMessage("no 12 o'clock circle ink was found on the vertical ray")
            .That(double.IsNaN(first)).IsFalse();
        Check.That(double.IsNaN(last)).IsFalse();
        return (first + last) / 2.0;
    }

    private static void WithTheme(ITheme theme, Action body)
    {
        var prior = Tokens.Current;
        try
        {
            Tokens.Activate(theme);
            body();
        }
        finally
        {
            Tokens.Activate(prior);
        }
    }

    [StaFact]
    public void TheDisc_IsOneHundredDipAcross()
    {
        // Rendered, not declared: a 120 DIP face would paint a 120 DIP span. Mutation: any disc size
        // other than 2 x SharedButtonRadius → the span goes red.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var scene = new Scene(Colors.White, 0, hostDip: 140);
            var span = scene.Render().PaintedSpanDip(Colors.White, yDip: ActivationButtonFace.Diameter / 2);
            Check.WithCustomMessage($"the painted disc is {span:F1} DIP across, not 100")
                .That(span).IsCloseTo(ActivationButtonFace.Diameter, 1.0);
        });
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheSign_MatchesTheBoardsSize_AndSitsCentred(double scale)
    {
        // F1: the owner's board draws the ring menu ~56 DIP tall on the 100 DIP disc. The glyph's
        // own geometry is the source of the expected extent, so a resized canvas or a moved circle
        // cannot drift from the board unnoticed. Mutation: ContainerRatio.Medium → ~31 DIP tall.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var scene = new Scene(Colors.White, 0, hostDip: 100, scale: scale);
            var probe = scene.Render();
            var face = probe.At(20, 50);
            // The sign's own box. It stays inside this, and the corners of the disc's 100 DIP box
            // are the page (white), which would otherwise match a white sign on a white page.
            var area = new Rect(20, 20, 60, 60);
            var bounds = probe.InkBoundsDip(scene.Face.UpperIconStroke!.Stroke!, face, area);

            double glyphScale = (ActivationButtonFace.Diameter * ContainerRatio.Solo) / ActionIcons.GridSize;
            var source = ActivationIcons.RingMenu.Stroke!.Bounds;
            // Geometry.Bounds is the path centre-line; the drawn sign also carries the stroke's
            // half width on each side.
            double expectedWidth = (source.Width + ActionIcons.GridStrokeThickness) * glyphScale;
            double expectedHeight = (source.Height + ActionIcons.GridStrokeThickness) * glyphScale;

            Check.WithCustomMessage(
                    $"the sign is {bounds.Width:F1} x {bounds.Height:F1} DIP; the board's is "
                    + $"{expectedWidth:F1} x {expectedHeight:F1}")
                .That(bounds.Width).IsCloseTo(expectedWidth, 1.5);
            Check.That(bounds.Height).IsCloseTo(expectedHeight, 1.5);
            Check.WithCustomMessage("the ticket's bigger sign is 52-60 DIP across")
                .That(bounds.Height).IsStrictlyGreaterThan(52.0);
            Check.That(bounds.Height).IsStrictlyLessThan(60.0);

            // Centred on the disc's centre (which the window centres on B).
            double centre = ActivationButtonFace.Diameter / 2;
            Check.WithCustomMessage($"the sign's centre is ({bounds.Left + (bounds.Width / 2):F2}, "
                    + $"{bounds.Top + (bounds.Height / 2):F2}), not ({centre}, {centre})")
                .That(bounds.Left + (bounds.Width / 2)).IsCloseTo(centre, 0.5);
            Check.That(bounds.Top + (bounds.Height / 2)).IsCloseTo(centre, 0.5);

            // Fully inside the inset content clip, with the ink clearance rule.
            var clip = (EllipseGeometry)((Grid)scene.Face.UpperContent.Parent).Clip;
            double maxRadius = probe.MaxInkRadiusDip(scene.Face.UpperIconStroke.Stroke!, face, area);
            Check.WithCustomMessage(
                    $"the sign's ink reaches {maxRadius:F1} DIP from the centre; the r{clip.RadiusX} "
                    + $"clip less {ActivationButtonFace.ContentInkClearance} DIP allows "
                    + $"{clip.RadiusX - ActivationButtonFace.ContentInkClearance:F1}")
                .That(maxRadius).IsLessOrEqualThan(
                    clip.RadiusX - ActivationButtonFace.ContentInkClearance);
        });
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheSign_IsSixOpenCircles_WithGaps(double scale)
    {
        // The board's look: six distinct small OPEN circles, not filled dots, a ring or a lens.
        // Three rendered probes, each of which tells the open rings from filled dots:
        //   1. outside the rings, through their outer edge: the board's six circles give 6 arcs
        //      with clear gaps (a five-circle or a lens mutant fails here);
        //   2. on the ring through the six small CENTRES: an open circle is crossed twice there
        //      (inner and outer ink bands), so the board gives 12 runs — six filled dots give 6
        //      (review-01 R1);
        //   3. each small circle's centre pixel is the face colour (the hole), not ink.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var scene = new Scene(Colors.White, 0, hostDip: 100, scale: scale);
            var probe = scene.Render();
            var face = probe.At(20, 50);

            double glyphScale = (ActivationButtonFace.Diameter * ContainerRatio.Solo) / ActionIcons.GridSize;

            // 1. The path's bounds are the circles' centres' envelope; its half height is the ring
            //    radius plus one small radius, i.e. the circles' OUTER edge.
            double outerEdgeRadius = (ActivationIcons.RingMenu.Stroke!.Bounds.Height / 2) * glyphScale;
            int outerRuns = probe.InkRunsOnCircle(face, outerEdgeRadius);
            Check.WithCustomMessage(
                    $"the sign paints {outerRuns} arc(s) on the circles' outer edge at x{scale}; "
                    + "the board's six circles give 6")
                .That(outerRuns).IsEqualTo(6);

            // 2. The ring radius itself, the true circle through the centres. Measured on the
            //    rendered 12 o'clock circle: along the vertical ray its full ink extent is
            //    symmetric about the ring radius (inner band R-r-s/2 .. outer R+r+s/2), so the
            //    midpoint of the first and last ink pixel is R.
            double ringRadius = RingRadiusDip(probe, face);
            int centreRuns = probe.InkRunsOnCircle(face, ringRadius);
            Check.WithCustomMessage(
                    $"the sign paints {centreRuns} arc(s) on the circle through the small circles' "
                    + $"centres at x{scale}; six OPEN circles give 12 (two bands each), six filled "
                    + "dots give 6")
                .That(centreRuns).IsEqualTo(12);

            // 3. The hole at each of the six centres is the face colour. The circles start at
            //    12 o'clock, so the probe angles must too (review-02 N1: 0° started in a gap).
            for (int i = 0; i < 6; i++)
            {
                double angle = (i * Math.PI / 3) - (Math.PI / 2);
                double x = (ActivationButtonFace.Diameter / 2) + (ringRadius * Math.Cos(angle));
                double y = (ActivationButtonFace.Diameter / 2) + (ringRadius * Math.Sin(angle));
                Check.WithCustomMessage(
                        $"small circle {i} at x{scale} has ink in its centre; the owner's sign is "
                        + "six OPEN circles")
                    .That(Distance(probe.At(x, y), face)).IsLessOrEqualThan((int)Tolerance);
            }

            // The centre dot is filled, not open.
            var dot = probe.InkContrast(
                new Rect(46, 46, 8, 8), scene.Face.UpperIconFill!.Fill!, face);
            Check.That(dot).IsGreaterOrEqualThan(4.5);
        });
    }

    [StaFact]
    public void TheFill_RisesFromTheBottom()
    {
        // Paint only: at half progress the bottom half is accent and the top half is the face.
        // Mutation: fill from the top → the two probe points swap, red.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var empty = new Scene(Colors.White, 0);
            var emptyProbe = empty.Render();
            var face = emptyProbe.At(50, 94);
            Check.That(empty.Face.FillHeightDip).IsEqualTo(0);

            using var half = new Scene(Colors.White, 0.5);
            Check.That(half.Face.FillHeightDip).IsCloseTo(50, 1e-9);
            var halfProbe = half.Render();
            var bottom = halfProbe.At(50, 94);
            var top = halfProbe.At(50, 6);
            Check.WithCustomMessage("the bottom half is still the face, not the accent fill")
                .That(Distance(bottom, face)).IsStrictlyGreaterThan(30);
            Check.WithCustomMessage("the top half is not the face: the fill came from the top")
                .That(Distance(top, face)).IsStrictlyLessThan(10);

            using var full = new Scene(Colors.White, 1.0);
            Check.That(full.Face.FillHeightDip).IsCloseTo(100, 1e-9);
            var fullProbe = full.Render();
            Check.WithCustomMessage("a full disc is not fully filled")
                .That(Distance(fullProbe.At(50, 94), fullProbe.At(50, 6))).IsStrictlyLessThan(10);
            Check.WithCustomMessage("a full disc still shows the face above the centre")
                .That(Distance(fullProbe.At(50, 6), face)).IsStrictlyGreaterThan(30);
        });
    }

    [StaFact]
    public void TheFill_ClipsToTheDisc_AndTheClipsMeetAtTheFillLine()
    {
        // The fill never paints the rim, and the two content copies split exactly on the line.
        // Mutation: no clip on the second copy → its bounds are the whole disc, red here and in
        // TheLowerCopy_OnlyPaintsBelowTheLine.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var half = new Scene(Colors.White, 0.5);
            var upper = ((RectangleGeometry)half.Face.UpperContent.Clip).Bounds;
            var lower = ((RectangleGeometry)half.Face.LowerContent.Clip).Bounds;
            Check.That(upper.Height).IsCloseTo(50, 1e-9);
            Check.That(upper.Bottom).IsCloseTo(50, 1e-9);
            Check.That(lower.Top).IsCloseTo(50, 1e-9);
            Check.That(lower.Height).IsCloseTo(50, 1e-9);

            using var empty = new Scene(Colors.White, 0);
            Check.That(((RectangleGeometry)empty.Face.UpperContent.Clip).Bounds.Height).IsCloseTo(100, 1e-9);
            Check.That(((RectangleGeometry)empty.Face.LowerContent.Clip).Bounds.Height).IsEqualTo(0);

            using var full = new Scene(Colors.White, 1);
            Check.That(((RectangleGeometry)full.Face.UpperContent.Clip).Bounds.Height).IsEqualTo(0);
            Check.That(((RectangleGeometry)full.Face.LowerContent.Clip).Bounds.Height).IsCloseTo(100, 1e-9);

            // The clip host's ellipse keeps the fill inside the rim AND leaves the 2 DIP face band
            // that separates accent from rim at full progress (F3).
            var discClip = (EllipseGeometry)((Grid)half.Face.Children[1]).Clip;
            Check.That(discClip.RadiusX).IsCloseTo(
                (ActivationButtonFace.Diameter / 2)
                - ActivationButtonFace.RimThickness
                - ActivationButtonFace.FillInset, 1e-9);
        });
    }

    [StaFact]
    public void TheLowerCopy_OnlyPaintsBelowTheLine()
    {
        // At p=0 the sign is the upper (text-coloured) copy only; the lower copy is clipped to
        // nothing. At p=1 the roles swap and the lower (face-coloured) copy is the whole sign.
        // Mutation: no clip on the lower copy → it paints face colour over the text copy and the
        // upper contrast finds no body pixels.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var empty = new Scene(Colors.White, 0);
            var emptyProbe = empty.Render();
            var face = emptyProbe.At(20, 50);
            var signBox = Bounds(empty.Face.UpperIconCanvas, empty.Face);
            var upper = emptyProbe.InkContrast(signBox, empty.Face.UpperIconStroke!.Stroke!, face);
            Check.WithCustomMessage($"the sign over the face is only {upper:F2}:1")
                .That(upper).IsGreaterOrEqualThan(4.5);

            using var full = new Scene(Colors.White, 1);
            var fullProbe = full.Render();
            var fill = fullProbe.At(50, 94);
            var lower = fullProbe.InkContrast(
                Bounds(full.Face.LowerContent, full.Face), full.Face.LowerIconStroke!.Stroke!, fill);
            Check.WithCustomMessage($"the sign over the fill is only {lower:F2}:1")
                .That(lower).IsGreaterOrEqualThan(4.5);
        });
    }

    [StaFact]
    public void Paused_DimsTheFill_ButNotTheSign()
    {
        // Mutation: dim never applied → opacity stays 1 and the rendered pixel equals the live one.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var live = new Scene(Colors.White, 0.5, paused: false);
            using var held = new Scene(Colors.White, 0.5, paused: true);

            Check.That(live.Face.Fill.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("the paused fill is not using the shared cue")
                .That(held.Face.Fill.Opacity).IsEqualTo(DwellPausedCue.ProgressOpacity);
            Check.That(held.Face.Fill.Opacity)
                .IsEqualTo((double)DwellPausedOpacityConverter.Instance.Convert(
                    true, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
            Check.That(held.Face.UpperContent.Opacity).IsEqualTo(1.0);

            var liveProbe = live.Render();
            var heldProbe = held.Render();
            var livePixel = liveProbe.At(50, 94);
            var heldPixel = heldProbe.At(50, 94);
            var face = liveProbe.At(50, 6);
            Check.WithCustomMessage("the held fill is not dimmer than the live one")
                .That(Contrast(heldPixel, face)).IsStrictlyLessThan(Contrast(livePixel, face));
            Check.WithCustomMessage("the held fill vanished")
                .That(Contrast(heldPixel, face)).IsStrictlyGreaterThan(1.2);
        });
    }

    [StaTheory]
    [MemberData(nameof(Themes))]
    public void EveryTheme_KeepsThePairsAboveTheirFloors(string themeName)
    {
        WithTheme(ThemeRegistry.Resolve(themeName), () =>
        {
            foreach (var page in new[] { Colors.White, Colors.Black })
            {
                var pageName = page == Colors.White ? "white" : "black";
                using var empty = new Scene(page, 0);
                var emptyProbe = empty.Render();
                var face = emptyProbe.At(20, 50);
                var signBox = Bounds(empty.Face.UpperIconCanvas, empty.Face);

                using var full = new Scene(page, 1);
                var fullProbe = full.Render();
                var fill = fullProbe.At(50, 94);
                var lowerSignBox = Bounds(full.Face.LowerContent, full.Face);

                // 1. sign over the face (text colour, both layers share it).
                var upperSign = emptyProbe.InkContrast(
                    signBox, empty.Face.UpperIconStroke!.Stroke!, face);
                var upperDot = emptyProbe.InkContrast(
                    new Rect(46, 46, 8, 8), empty.Face.UpperIconFill!.Fill!, face);
                Check.WithCustomMessage($"{themeName}/{pageName}: sign on the face {upperSign:F2}:1")
                    .That(upperSign).IsGreaterOrEqualThan(4.5);
                Check.WithCustomMessage($"{themeName}/{pageName}: dot on the face {upperDot:F2}:1")
                    .That(upperDot).IsGreaterOrEqualThan(4.5);
                // 2. sign over the fill (face colour).
                var lowerSign = fullProbe.InkContrast(
                    lowerSignBox, full.Face.LowerIconStroke!.Stroke!, fill);
                var lowerDot = fullProbe.InkContrast(
                    new Rect(46, 46, 8, 8), full.Face.LowerIconFill!.Fill!, fill);
                Check.WithCustomMessage($"{themeName}/{pageName}: sign on the fill {lowerSign:F2}:1")
                    .That(lowerSign).IsGreaterOrEqualThan(4.5);
                Check.WithCustomMessage($"{themeName}/{pageName}: dot on the fill {lowerDot:F2}:1")
                    .That(lowerDot).IsGreaterOrEqualThan(4.5);

                // 3. the fill reads as a distinct surface from the face.
                var fillVersusFace = Contrast(fill, face);
                Check.WithCustomMessage($"{themeName}/{pageName}: fill on the face {fillVersusFace:F2}:1")
                    .That(fillVersusFace).IsGreaterOrEqualThan(3.0);

                // 4. the disc's visible edge against the page at progress 0, 0.5 and 1 (F3): the rim,
                //    or the face band just inside it where the rim is the page colour (Contrast's
                //    white rim on a white page). The band must survive as the fill rises — without
                //    the 2 DIP inset the bottom edge becomes cyan-vs-white through a white rim.
                var edgeLog = new System.Text.StringBuilder();
                foreach (var progress in new[] { 0.0, 0.5, 1.0 })
                {
                    using var edgeScene = new Scene(page, progress);
                    var edgeProbe = edgeScene.Render();
                    var band = edgeProbe.At(50, 97);   // r = 47: face between fill clip (46) and rim (48)
                    var rim = edgeProbe.At(50, 99);
                    var edgeAt = Math.Max(Contrast(rim, page), Contrast(band, page));
                    Check.WithCustomMessage(
                            $"{themeName}/{pageName} at p={progress}: the disc edge is {edgeAt:F2}:1 "
                            + $"(rim {Contrast(rim, page):F2}, band {Contrast(band, page):F2})")
                        .That(edgeAt).IsGreaterOrEqualThan(3.0);
                    edgeLog.Append($" p{progress}={edgeAt:F2}(rim {Contrast(rim, page):F2})");
                }

                _output.WriteLine(
                    $"MEASURED {themeName}/{pageName}: sign-face={upperSign:F2} dot-face={upperDot:F2} "
                    + $"sign-fill={lowerSign:F2} dot-fill={lowerDot:F2} fill-face={fillVersusFace:F2} "
                    + $"edge:{edgeLog} face-page={Contrast(face, page):F2}");
            }
        });
    }

    [StaTheory]
    [MemberData(nameof(Themes))]
    public void EveryTheme_KeepsTheHeldSignReadable_AboveFourPointFive(string themeName)
    {
        // The sign sits across the disc, so at 0.75 most of it is over the fill. Dimming the fill
        // must not take the sign under 4.5:1. Mutation: dim the sign with the fill → red.
        WithTheme(ThemeRegistry.Resolve(themeName), () =>
        {
            using var held = new Scene(Colors.White, 0.75, paused: true);
            var probe = held.Render();
            var fill = probe.At(50, 94);
            var sign = probe.InkContrast(
                Bounds(held.Face.LowerContent, held.Face), held.Face.LowerIconStroke!.Stroke!, fill);
            var dot = probe.InkContrast(
                new Rect(46, 46, 8, 8), held.Face.LowerIconFill!.Fill!, fill);
            Check.WithCustomMessage($"{themeName}: sign on the held fill {sign:F2}:1")
                .That(sign).IsGreaterOrEqualThan(4.5);
            Check.WithCustomMessage($"{themeName}: dot on the held fill {dot:F2}:1")
                .That(dot).IsGreaterOrEqualThan(4.5);
            _output.WriteLine($"MEASURED {themeName}: sign-held-fill={sign:F2} dot-held-fill={dot:F2}");
        });
    }

    [StaFact]
    public void TheFace_UsesTheTokenBrushes_AndTheOnFillCopy_UsesTheFaceColour()
    {
        // Structural half of the colour story: the face and rim are the toolbar's tokens, the fill
        // is the accent, and the lower copy is the face colour so both halves read as negative
        // space. Mutation: a literal brush → red here even where a palette happens to pass contrast.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var scene = new Scene(Colors.White, 0.5);
            Check.That(scene.Face.Face.Fill).IsSameReferenceAs(Tokens.BrushSurfaceMedium);
            Check.That(scene.Face.Face.Stroke).IsSameReferenceAs(Tokens.BrushBorderSubtle);
            Check.That(scene.Face.Fill.Fill).IsSameReferenceAs(Tokens.BrushAccent);
            Check.That(scene.Face.UpperIconStroke!.Stroke).IsSameReferenceAs(Tokens.BrushTextPrimary);
            Check.That(scene.Face.UpperIconFill!.Fill).IsSameReferenceAs(Tokens.BrushTextPrimary);
            Check.That(scene.Face.LowerIconStroke!.Stroke).IsSameReferenceAs(Tokens.BrushSurfaceMedium);
            Check.That(scene.Face.LowerIconFill!.Fill).IsSameReferenceAs(Tokens.BrushSurfaceMedium);
            Check.That(scene.Face.UpperIconStroke!.Data).IsSameReferenceAs(ActivationIcons.RingMenu.Stroke);
            Check.That(scene.Face.UpperIconFill!.Data).IsSameReferenceAs(ActivationIcons.RingMenu.Fill);
        });
    }

    [StaFact]
    public void ReviewPngs_WhenAsked()
    {
        // Ticket evidence, not a gate: EYECTRL_F1_REVIEW_DIR names a folder and this writes the
        // sign at progress 0, 0.5 and 1 in Midnight on a white and a dark page, at the user's 2.0
        // (100 DIP face -> 200 x 200 px). Nothing is shown: the face is drawn through
        // RenderTargetBitmap with no window. A plain run writes nothing.
        var directory = Environment.GetEnvironmentVariable("EYECTRL_F1_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        WithTheme(MidnightTheme.Instance, () =>
        {
            foreach (var (pageName, page) in new[] { ("white", Colors.White), ("dark", Colors.Black) })
                foreach (var progress in new[] { 0.0, 0.5, 1.0 })
                {
                    using var scene = new Scene(page, progress);
                    var bitmap = scene.RenderBitmap();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = Path.Combine(
                        directory, $"f1-midnight-{pageName}-{progress * 100:000}.png");
                    using var file = File.Create(path);
                    encoder.Save(file);
                    _output.WriteLine($"WROTE {path}");
                }
        });
    }
}
