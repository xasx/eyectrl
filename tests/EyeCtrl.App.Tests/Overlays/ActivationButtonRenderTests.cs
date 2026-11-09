namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// B2's activation button face, rendered headlessly at the user's 2.0. Nothing here shows a window:
/// the production <see cref="ActivationButtonFace"/> is arranged in a plain host and drawn through a
/// <see cref="RenderTargetBitmap"/>; the probes read rendered pixels, never brush declarations
/// alone.
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
            ActivationButtonContent content = ActivationButtonContent.Magnifier,
            string label = "Magnifier",
            double hostDip = 100,
            double scale = Scale)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            _scale = scale;
            _hostDip = hostDip;
            _offset = (hostDip - ActivationButtonFace.Diameter) / 2;

            Face = new ActivationButtonFace();
            Face.SetContent(content, label);
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
            // The clips/height change AFTER the constructor's layout pass; RenderTargetBitmap does
            // not lay out, so run the pass here or the old geometry renders.
            _host.Measure(new Size(_hostDip, _hostDip));
            _host.Arrange(new Rect(0, 0, _hostDip, _hostDip));
            _host.UpdateLayout();

            int pixels = (int)Math.Round(_hostDip * _scale);
            var bitmap = new RenderTargetBitmap(
                pixels, pixels, 96.0 * _scale, 96.0 * _scale, PixelFormats.Pbgra32);
            bitmap.Render(_host);

            var stride = pixels * 4;
            var data = new byte[stride * pixels];
            bitmap.CopyPixels(data, stride, 0);
            return new Probe(data, pixels, _scale, _offset);
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
                if (Distance(pixel, page) <= 24) continue;
                first = Math.Min(first, x);
                last = Math.Max(last, x);
            }

            return last < first ? 0 : (last - first + 1) / _scale;
        }

        /// <summary>The painted horizontal span (DIP) on one row inside an x range, against a
        /// background colour — the lens-size probe scans the lens's centre row this way.</summary>
        public double SpanDip(Color background, double yDip, double x0, double x1)
        {
            int y = Math.Clamp((int)Math.Round((yDip + _offset) * _scale), 0, _width - 1);
            int from = Math.Max(0, (int)Math.Ceiling((x0 + _offset) * _scale));
            int to = Math.Min(_width - 1, (int)Math.Floor((x1 + _offset) * _scale));
            int first = int.MaxValue;
            int last = int.MinValue;
            for (var x = from; x <= to; x++)
            {
                var i = ((y * _width) + x) * 4;
                var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                if (Distance(pixel, background) <= 24) continue;
                first = Math.Min(first, x);
                last = Math.Max(last, x);
            }

            return last < first ? 0 : (last - first + 1) / _scale;
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
                    if (Distance(pixel, expected) <= 24)
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
        var colour = ((SolidColorBrush)brush).Color;
        return Blend(Color.FromRgb(colour.R, colour.G, colour.B), background, colour.A / 255.0);
    }

    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

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

    [StaFact]
    public void TheMagnifier_MatchesTheBoardsLensSize()
    {
        // F2: the owner's board (look-b-word) draws the lens at 2 x 13 + 3.6 ≈ 29.6 DIP on the
        // 100 DIP disc. Scaled by ContainerRatio.Large the build draws ≈29.7; the old Medium arm
        // gave ≈23.7. Rendered pixels, so a silent shrink goes red.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var scene = new Scene(Colors.White, 0);
            var probe = scene.Render();
            var icon = Bounds(scene.Face.UpperIconCanvas, scene.Face);
            double scale = icon.Width / ActionIcons.GridSize;
            double lensY = icon.Top + (10.5 * scale);
            double x0 = icon.Left + (2.5 * scale) - 2;
            double x1 = icon.Left + (18.5 * scale) + 2;

            var face = probe.At(20, 50);
            double span = probe.SpanDip(face, lensY, x0, x1);
            Check.WithCustomMessage($"the drawn lens is {span:F1} DIP across; the board's is 29.6")
                .That(span).IsStrictlyGreaterThan(28.0);
            Check.WithCustomMessage($"the drawn lens is {span:F1} DIP across; the board's is 29.6")
                .That(span).IsStrictlyLessThan(31.5);
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
        // TheContentCopy_OverTheFill_DoesNotPaintAboveTheLine.
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
    public void TheContentCopy_OverTheFill_DoesNotPaintAboveTheLine()
    {
        // At progress 0 the label is the text colour; the lower copy (face-coloured) must be
        // invisible. Mutation: no clip on the second copy → it paints the face colour over the text,
        // and no text-coloured body pixels remain in the label's box.
        WithTheme(MidnightTheme.Instance, () =>
        {
            using var empty = new Scene(Colors.White, 0);
            var probe = empty.Render();
            var face = probe.At(20, 50);
            var label = Bounds(empty.Face.UpperLabel, empty.Face);
            var contrast = probe.InkContrast(label, empty.Face.UpperLabel.Foreground, face);
            Check.WithCustomMessage($"the label over the face is only {contrast:F2}:1")
                .That(contrast).IsGreaterOrEqualThan(4.5);
        });
    }

    [StaFact]
    public void Paused_DimsTheFill_ButNotTheContent()
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

                using var full = new Scene(page, 1);
                var fullProbe = full.Render();
                var fill = fullProbe.At(50, 94);

                // 1. content over the face (text colour): label and icon body.
                var upperLabel = emptyProbe.InkContrast(
                    Bounds(empty.Face.UpperLabel, empty.Face), empty.Face.UpperLabel.Foreground, face);
                var upperIcon = emptyProbe.InkContrast(
                    Bounds(empty.Face.UpperIconCanvas, empty.Face), empty.Face.UpperIconStroke!.Stroke!, face);
                Check.WithCustomMessage($"{themeName}/{pageName}: label on the face {upperLabel:F2}:1")
                    .That(upperLabel).IsGreaterOrEqualThan(4.5);
                Check.WithCustomMessage($"{themeName}/{pageName}: icon on the face {upperIcon:F2}:1")
                    .That(upperIcon).IsGreaterOrEqualThan(4.5);
                // 2. content over the fill (face colour): label and icon body.
                var lowerLabel = fullProbe.InkContrast(
                    Bounds(full.Face.LowerLabel, full.Face), full.Face.LowerLabel.Foreground, fill);
                var lowerIcon = fullProbe.InkContrast(
                    Bounds(full.Face.UpperIconCanvas, full.Face), full.Face.LowerIconStroke!.Stroke!, fill);
                Check.WithCustomMessage($"{themeName}/{pageName}: label on the fill {lowerLabel:F2}:1")
                    .That(lowerLabel).IsGreaterOrEqualThan(4.5);
                Check.WithCustomMessage($"{themeName}/{pageName}: icon on the fill {lowerIcon:F2}:1")
                    .That(lowerIcon).IsGreaterOrEqualThan(4.5);

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
                    $"MEASURED {themeName}/{pageName}: label-face={upperLabel:F2} icon-face={upperIcon:F2} "
                    + $"label-fill={lowerLabel:F2} icon-fill={lowerIcon:F2} fill-face={fillVersusFace:F2} "
                    + $"edge:{edgeLog} face-page={Contrast(face, page):F2}");
            }
        });
    }

    [StaTheory]
    [MemberData(nameof(Themes))]
    public void EveryTheme_KeepsTheHeldFillReadable_AboveFourPointFive(string themeName)
    {
        // The label sits below half progress, so at 0.5 it is entirely over the fill. Dimming the
        // fill must not take the word under 4.5:1. Mutation: dim the content with the fill → red.
        WithTheme(ThemeRegistry.Resolve(themeName), () =>
        {
            using var held = new Scene(Colors.White, 0.5, paused: true);
            var probe = held.Render();
            var fill = probe.At(50, 94);
            var contrast = probe.InkContrast(
                Bounds(held.Face.LowerLabel, held.Face), held.Face.LowerLabel.Foreground, fill);
            Check.WithCustomMessage($"{themeName}: label on the held fill {contrast:F2}:1")
                .That(contrast).IsGreaterOrEqualThan(4.5);
            _output.WriteLine($"MEASURED {themeName}: label-held-fill={contrast:F2}");
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
            Check.That(scene.Face.UpperLabel.Foreground).IsSameReferenceAs(Tokens.BrushTextPrimary);
            Check.That(scene.Face.LowerLabel.Foreground).IsSameReferenceAs(Tokens.BrushSurfaceMedium);
            Check.That(scene.Face.UpperIconStroke!.Data).IsSameReferenceAs(ActivationIcons.Magnifier.Stroke);
        });
    }
}
