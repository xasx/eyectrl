namespace EyeCtrl.App.Tests;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Rest activation R2: the cursor ring draws the fill. Everything here is HEADLESS — the window is
/// constructed, its content detached and rendered through <see cref="RenderTargetBitmap"/>; no
/// window is ever shown, and nothing is written to the real profile.
///
/// <para>
/// The owner's AchromaticReticle at 200 DIP / 1.5 thickness is the reference look: the ring's two
/// bands give both polarities (a near-white outer band, a near-black core), and the fill is the
/// same bands HEAVIER (<see cref="FiredFlash.PeakStack"/>). The arc is sampled at 90 DIP, inside
/// the fill's band and OUTSIDE the rest ring's own band (which ends at 94.5), so a painted pixel
/// there is the fill and nothing else. The contrast probe samples only radii the fill owns.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class CursorRingFillRenderTests
{
    private const double Size = 200.0;
    private const double Thickness = 1.5;

    /// <summary>Inside the fill's outer band (87.5..100) and outside the rest ring's (94.5..100).</summary>
    private const double ExclusiveOuterRadius = 90.0;

    /// <summary>Inside the fill's core band (95.5..100) but outside the rest ring's core (98.5..100).</summary>
    private const double ExclusiveCoreRadius = 97.0;

    private static CursorSettings OwnerLook(CursorRingStyle style = CursorRingStyle.AchromaticReticle) => new()
    {
        RingStyle = style,
        CircleSize = Size,
        CircleThickness = Thickness,
        CircleOpacity = 1.0,
        CircleColor = "#A5F3FC",
    };

    /// <summary>One headless frame: the real window's content, laid out over an opaque background.</summary>
    private sealed class FillScene : IDisposable
    {
        private readonly Grid _host;
        private readonly double _scale;

        public CursorCircleWindow Window { get; }

        public FillScene(double scale, CursorSettings settings, Color background)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            DpiHelper.Initialize(scale, scale);
            _scale = scale;

            Window = new CursorCircleWindow();
            Window.SetPinned(true);                    // the fill only shows pinned (window opacity 1)
            Window.UpdateAppearance(settings);

            var content = (FrameworkElement)Window.Content;
            Window.Content = null;                     // detach so the canvas can be hosted headlessly

            _host = new Grid
            {
                Width = Size,
                Height = Size,
                Background = new SolidColorBrush(background),
            };
            _host.Children.Add(content);
            _host.Measure(new Size(Size, Size));
            _host.Arrange(new Rect(0, 0, Size, Size));
            _host.UpdateLayout();
        }

        public ProbeRaster Render()
        {
            // UpdateFill changes Visibility and Data AFTER the constructor's layout pass; the tree
            // is disconnected, so nothing schedules another one. RenderTargetBitmap does not lay
            // out — run the pass here or the arc renders at its stale (collapsed) geometry.
            _host.Measure(new Size(Size, Size));
            _host.Arrange(new Rect(0, 0, Size, Size));
            _host.UpdateLayout();

            var pixelsWide = (int)Math.Round(Size * _scale);
            var bitmap = new RenderTargetBitmap(
                pixelsWide, pixelsWide, 96.0 * _scale, 96.0 * _scale, PixelFormats.Pbgra32);
            bitmap.Render(_host);

            var stride = pixelsWide * 4;
            var pixels = new byte[stride * pixelsWide];
            bitmap.CopyPixels(pixels, stride, 0);
            return new ProbeRaster(pixels, pixelsWide, _scale);
        }

        public void Dispose() => Window.Close();
    }

    /// <summary>A rendered frame in device pixels, queried in DIP polar coordinates.</summary>
    private sealed class ProbeRaster
    {
        private readonly byte[] _pixels;
        private readonly int _width;
        private readonly double _scale;

        public ProbeRaster(byte[] pixels, int width, double scale)
        {
            _pixels = pixels;
            _width = width;
            _scale = scale;
        }

        public Color At(double xDip, double yDip)
        {
            var x = Math.Clamp((int)Math.Round(xDip * _scale), 0, _width - 1);
            var y = Math.Clamp((int)Math.Round(yDip * _scale), 0, _width - 1);
            var i = ((y * _width) + x) * 4;
            return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }

        public Color AtAngle(double degrees, double radiusDip)
        {
            var radians = degrees * Math.PI / 180.0;
            return At(
                (Size / 2) + (radiusDip * Math.Sin(radians)),
                (Size / 2) - (radiusDip * Math.Cos(radians)));
        }

        /// <summary>Painted = the pixel is not (within a small antialiasing tolerance) the background.</summary>
        public bool PaintedAt(double degrees, double radiusDip, Color background)
        {
            var pixel = AtAngle(degrees, radiusDip);
            var distance = Math.Abs(pixel.R - background.R)
                + Math.Abs(pixel.G - background.G)
                + Math.Abs(pixel.B - background.B);
            return distance > 30;
        }

        /// <summary>The best WCAG contrast any pixel in the radial annulus has against the backdrop —
        /// what the eye reads as the ring's strongest edge (the two-polarity rule C1 uses).</summary>
        public double BestContrastInAnnulus(double innerDip, double outerDip, Color backdrop)
        {
            var best = 1.0;
            var innerPx = innerDip * _scale;
            var outerPx = outerDip * _scale;
            var centre = Size / 2 * _scale;

            for (var y = 0; y < _width; y++)
                for (var x = 0; x < _width; x++)
                {
                    var dx = x + 0.5 - centre;
                    var dy = y + 0.5 - centre;
                    var r = Math.Sqrt((dx * dx) + (dy * dy));
                    if (r < innerPx || r > outerPx)
                        continue;

                    var i = ((y * _width) + x) * 4;
                    var pixel = Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
                    best = Math.Max(best, Contrast(pixel, backdrop));
                }

            return best;
        }
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>Production's own palette source: <c>UpdateAppearance</c> passes these two tokens.</summary>
    private static string Hex(Color c) =>
        "#" + c.R.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.G.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.B.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

    private static CursorRingStack OwnerStack() => CursorRingStacks.For(
        CursorRingStyle.AchromaticReticle, Size, Thickness, 1.0,
        "#A5F3FC", Hex(Tokens.StrokeHalo), Hex(Tokens.TextPrimary));

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheFill_CoversTwelveOClockClockwise(double scale)
    {
        // R2-M1 (counter-clockwise) fills the top-LEFT quadrant; R2-M2 (start at 3 o'clock) fills the
        // bottom-right one. Both leave the top-right empty at p = 0.25, where this asserts paint.
        var priorX = DpiHelper.ScaleX;
        var priorY = DpiHelper.ScaleY;
        try
        {
            foreach (var (progress, lastPainted, firstClear) in new[] { (0.25, 85.0, 95.0), (0.5, 175.0, 185.0) })
            {
                using var scene = new FillScene(scale, OwnerLook(), Colors.Black);
                scene.Window.UpdateFill(progress, paused: false);
                var raster = scene.Render();

                for (var angle = 5.0; angle <= lastPainted; angle += 5.0)
                    Check.WithCustomMessage(
                            $"p={progress} at {angle:0}°: the arc is not painted (it must sweep clockwise from 12 o'clock).")
                        .That(raster.PaintedAt(angle, ExclusiveOuterRadius, Colors.Black)).IsTrue();

                for (var angle = firstClear; angle <= 355.0; angle += 5.0)
                    Check.WithCustomMessage(
                            $"p={progress} at {angle:0}°: painted outside the clockwise sector from 12 o'clock.")
                        .That(raster.PaintedAt(angle, ExclusiveOuterRadius, Colors.Black)).IsFalse();
            }

            // Full: the whole ring, at the same radius nothing but the fill reaches.
            using var full = new FillScene(scale, OwnerLook(), Colors.Black);
            full.Window.UpdateFill(1.0, paused: false);
            var fullRaster = full.Render();
            for (var angle = 0.0; angle < 360.0; angle += 5.0)
                Check.WithCustomMessage($"p=1 at {angle:0}°: a gap in the full arc.")
                    .That(fullRaster.PaintedAt(angle, ExclusiveOuterRadius, Colors.Black)).IsTrue();
        }
        finally
        {
            DpiHelper.Initialize(priorX, priorY);
        }
    }

    [StaFact]
    public void TheFill_UsesTheRingsOwnStack_Heavier_WithNoColourOfItsOwn()
    {
        // R2-M3: a fixed colour instead of the stack. The arc's brushes must be the rest stack's
        // PeakStack bands — same colours/opacities as the ring, only wider — in every style.
        var owner = OwnerStack();
        var peak = FiredFlash.PeakStack(owner, Size);

        foreach (var style in new[]
                 {
                     CursorRingStyle.AchromaticReticle,
                     CursorRingStyle.WeightedSandwich,
                     CursorRingStyle.BiTonalSegments,
                 })
        {
            var stack = CursorRingStacks.For(
                style, Size, Thickness, 1.0, "#A5F3FC", Hex(Tokens.StrokeHalo), Hex(Tokens.TextPrimary));
            var heavier = FiredFlash.PeakStack(stack, Size);

            using var scene = new FillScene(1.0, OwnerLook(style), Colors.Black);
            scene.Window.UpdateFill(0.5, paused: false);

            Check.That(((SolidColorBrush)scene.Window.FillArcOuter.Stroke).Color)
                .IsEqualTo(Parse(heavier.Outer.Hex));
            Check.That(((SolidColorBrush)scene.Window.FillArcCore.Stroke).Color)
                .IsEqualTo(Parse(heavier.Core.Hex));
            Check.That(scene.Window.FillArcOuter.StrokeThickness).IsEqualTo(heavier.Outer.Thickness);
            Check.That(scene.Window.FillArcCore.StrokeThickness).IsEqualTo(heavier.Core.Thickness);

            // The fill is a solid sweep in every style; the ring keeps its own dashes.
            Check.That(scene.Window.FillArcOuter.StrokeDashArray).IsEmpty();
            Check.That(scene.Window.FillArcCore.StrokeDashArray).IsEmpty();
        }

        Check.That(peak.Outer.Thickness).IsStrictlyGreaterThan(owner.Outer.Thickness);
        Check.That(peak.Core.Thickness).IsStrictlyGreaterThan(owner.Core.Thickness);
    }

    [StaFact]
    public void TheOwnersFillArc_PaintsTheStacksTwoPolarities()
    {
        // The rendered pixels, not just the brushes: on white the dark core is the edge, on black
        // the light outer band is. These are the ring's own colours — nothing new is introduced.
        var stack = OwnerStack();

        using (var onWhite = new FillScene(1.0, OwnerLook(), Colors.White))
        {
            onWhite.Window.UpdateFill(1.0, paused: false);
            var raster = onWhite.Render();
            Check.WithCustomMessage("the fill's core is not the ring's core colour")
                .That(raster.AtAngle(0, ExclusiveCoreRadius)).IsEqualTo(Parse(stack.Core.Hex));
        }

        using (var onBlack = new FillScene(1.0, OwnerLook(), Colors.Black))
        {
            onBlack.Window.UpdateFill(1.0, paused: false);
            var raster = onBlack.Render();
            Check.WithCustomMessage("the fill's outer band is not the ring's outer band colour")
                .That(raster.AtAngle(0, ExclusiveOuterRadius)).IsEqualTo(Parse(stack.Outer.Hex));
        }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheOwnersFillArc_StaysAtThreeToOneOnWhiteAndBlack(double scale)
    {
        // The radii are the fill's OWN bands: 88..94 is the heavier outer band alone (the rest ring
        // ends at 94.5), 96..98 is the heavier core alone (the rest core starts at 98.5).
        var priorX = DpiHelper.ScaleX;
        var priorY = DpiHelper.ScaleY;
        try
        {
            foreach (var (hex, backdrop) in new[] { ("#FFFFFF", Colors.White), ("#000000", Colors.Black) })
                foreach (var paused in new[] { false, true })
                {
                    using var scene = new FillScene(scale, OwnerLook(), backdrop);
                    scene.Window.UpdateFill(1.0, paused);
                    var raster = scene.Render();

                    var outerBand = raster.BestContrastInAnnulus(88.0, 94.0, backdrop);
                    var coreBand = raster.BestContrastInAnnulus(96.0, 98.0, backdrop);
                    var best = Math.Max(outerBand, coreBand);

                    Check.WithCustomMessage(
                            $"the owner's fill arc on {hex} (paused={paused}) is only {best:F2}:1 "
                            + $"(outer {outerBand:F2}, core {coreBand:F2}); the floor is 3:1.")
                        .That(best).IsGreaterOrEqualThan(3.0);
                }
        }
        finally
        {
            DpiHelper.Initialize(priorX, priorY);
        }
    }

    [StaFact]
    public void Paused_DimsBothBandsWithTheSharedCue_AndNeverHidesThem()
    {
        // R2-M6: paused at full opacity. The shared dwell-paused cue (0.7) applies to both bands.
        using var scene = new FillScene(1.0, OwnerLook(), Colors.Black);
        scene.Window.UpdateFill(0.5, paused: false);

        Check.That(scene.Window.FillArcOuter.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(scene.Window.FillArcCore.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(scene.Window.FillArcOuter.Opacity).IsEqualTo(1.0);
        Check.That(scene.Window.FillArcCore.Opacity).IsEqualTo(1.0);

        scene.Window.UpdateFill(0.5, paused: true);

        Check.That(scene.Window.FillArcOuter.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(scene.Window.FillArcCore.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(scene.Window.FillArcOuter.Opacity).IsCloseTo(DwellPausedCue.ProgressOpacity, 1e-12);
        Check.That(scene.Window.FillArcCore.Opacity).IsCloseTo(DwellPausedCue.ProgressOpacity, 1e-12);

        // And the rendered paused arc is dimmer than the live one, on the same band and backdrop.
        using var live = new FillScene(1.0, OwnerLook(), Colors.White);
        using var held = new FillScene(1.0, OwnerLook(), Colors.White);
        live.Window.UpdateFill(0.5, paused: false);
        held.Window.UpdateFill(0.5, paused: true);

        var livePixel = live.Render().AtAngle(0, ExclusiveCoreRadius);
        var heldPixel = held.Render().AtAngle(0, ExclusiveCoreRadius);
        Check.That(heldPixel).IsNotEqualTo(livePixel);
        Check.That(Contrast(heldPixel, Colors.White))
            .IsStrictlyLessThan(Contrast(livePixel, Colors.White));
        Check.WithCustomMessage("the paused arc became invisible")
            .That(Contrast(heldPixel, Colors.White)).IsStrictlyGreaterThan(1.0);
    }

    [StaFact]
    public void ZeroProgress_CollapsesTheArc_AndANonFiniteValueIsZero()
    {
        using var scene = new FillScene(1.0, OwnerLook(), Colors.Black);
        scene.Window.UpdateFill(0.5, paused: false);
        Check.That(scene.Window.FillArcOuter.Visibility).IsEqualTo(Visibility.Visible);

        scene.Window.UpdateFill(0, paused: false);
        Check.That(scene.Window.FillArcOuter.Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(scene.Window.FillArcCore.Visibility).IsEqualTo(Visibility.Collapsed);

        scene.Window.UpdateFill(double.NaN, paused: false);
        Check.That(scene.Window.FillArcOuter.Visibility).IsEqualTo(Visibility.Collapsed);
    }
}
