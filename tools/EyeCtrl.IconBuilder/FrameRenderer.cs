namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

internal static class FrameRenderer
{
    /// <summary>Renders one frame to straight-alpha BGRA pixels, top-down rows.</summary>
    public static byte[] RenderBgra(FramePlan plan)
    {
        var scale = plan.Size / IconSpec.GridSize;

        // Three compositing passes in the spec's layer order: tile, then glow, then body+.
        // The glow MUST be its own pass AFTER the tile — compositing it before an opaque
        // tile buried it completely (the tile is part of the top pass).
        var tile = new DrawingVisual();
        using (var dc = tile.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawTile(dc, plan);
            dc.Pop();
        }

        var rest = new DrawingVisual();
        using (var dc = rest.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawBody(dc);
            DrawTrack(dc, plan);
            if (plan.ArcRadius is double arcRadius)
                DrawArc(dc, plan, arcRadius, plan.ArcWidth!.Value);
            DrawPie(dc, plan);
            DrawDot(dc, plan);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(plan.Size, plan.Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(tile);

        // Soft bloom between tile and body. Deliberately NOT a BlurEffect: WPF effects do not
        // render under SoftwareOnly (A1's determinism requirement) and a rendered-root effect
        // is ignored by RenderTargetBitmap — the mockup's feGaussianBlur halo is approximated
        // with a multi-stop radial gradient instead, deterministic by construction.
        DrawingVisual? glow = null;
        if (plan.Glow)
        {
            glow = new DrawingVisual();
            using (var dc = glow.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                // Stops carry fractions of the frame's peak alpha (Gaussian-ish falloff of the
                // blurred disc: plateau, disc edge at 0.714 of the drawn radius, tail to 1.0).
                Color A(double fraction) => Color.FromArgb(
                    (byte)Math.Round(255 * plan.GlowPeakAlpha * fraction), 0xA5, 0xF3, 0xFC);
                var brush = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                    GradientStops =
                    {
                        new GradientStop(A(1.00), 0.00),
                        new GradientStop(A(0.96), 0.58),
                        new GradientStop(A(0.83), 0.714),
                        new GradientStop(A(0.54), 0.78),
                        new GradientStop(A(0.27), 0.86),
                        new GradientStop(A(0.10), 0.93),
                        new GradientStop(A(0.0), 1.0),
                    },
                };
                dc.DrawEllipse(brush, null, IconSpec.GlowCenter,
                    IconSpec.GlowRx + 12, IconSpec.GlowRy + 12);   // tail reaches 3σ past the disc
                dc.Pop();
            }
            rtb.Render(glow);
        }

        rtb.Render(rest);

        var pixels = ToStraightBgra(rtb);
        GC.KeepAlive(tile);
        GC.KeepAlive(glow);
        GC.KeepAlive(rest);
        return pixels;
    }

    public static BitmapSource ToBitmapSource(int size, byte[] bgra) =>
        BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);

    public static byte[] EncodePng(int size, byte[] bgra)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(size, bgra)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void DrawTile(DrawingContext dc, FramePlan plan)
    {
        var rect = new Rect(IconSpec.TileX, IconSpec.TileY, IconSpec.TileExtent, IconSpec.TileExtent);

        Brush fill;
        if (plan.FlatTile)
        {
            fill = new SolidColorBrush(IconSpec.TileFlat16);
        }
        else
        {
            // SVG radial (cx,cy,r) → WPF: Center = GradientOrigin = (0.5,0.38), RadiusX = RadiusY = 0.85.
            var brush = new RadialGradientBrush(IconSpec.TileLight, IconSpec.TileDark)
            {
                Center = new Point(0.5, 0.38),
                GradientOrigin = new Point(0.5, 0.38),
                RadiusX = 0.85,
                RadiusY = 0.85,
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            fill = brush;
        }
        dc.DrawRoundedRectangle(fill, null, rect, IconSpec.TileRadius, IconSpec.TileRadius);

        if (plan.Rim)
        {
            var rimRect = new Rect(IconSpec.TileX + 1.5, IconSpec.TileY + 1.5,
                IconSpec.TileExtent - 3, IconSpec.TileExtent - 3);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(IconSpec.Rim), 2),
                rimRect, IconSpec.TileRadius - 1.5, IconSpec.TileRadius - 1.5);
        }
    }

    private static void DrawBody(DrawingContext dc)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(48, IconSpec.BodyGradientTop),
            EndPoint = new Point(48, IconSpec.BodyGradientBottom),
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop(IconSpec.BodyTop, 0),
                new GradientStop(IconSpec.BodyBottom, 1),
            },
        };
        dc.DrawGeometry(brush, null, Geometry.Parse(IconSpec.BodyPath));
    }

    private static void DrawTrack(DrawingContext dc, FramePlan plan)
    {
        dc.DrawEllipse(new SolidColorBrush(IconSpec.Track),
            plan.Hairline ? new Pen(new SolidColorBrush(IconSpec.Hairline), 1) : null,
            new Point(48, 48), plan.TrackRadius, plan.TrackRadius);
    }

    private static void DrawArc(DrawingContext dc, FramePlan plan, double radius, double width)
    {
        var brush = new SolidColorBrush(IconSpec.Arc) { Opacity = plan.ArcAlpha };
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, Geometry.Parse(IconSpec.ArcPath(radius)));
    }

    private static void DrawPie(DrawingContext dc, FramePlan plan)
    {
        Brush brush;
        if (plan.PieGradient)
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new Point(60, 48),   // PieHead
                EndPoint = new Point(36, 44),     // PieTail
                MappingMode = BrushMappingMode.Absolute,
                GradientStops =
                {
                    new GradientStop(IconSpec.PieHead, 0),
                    new GradientStop(IconSpec.PieTail, 1),
                },
            };
        }
        else
        {
            brush = new SolidColorBrush(IconSpec.PieSolid16);
        }
        dc.DrawGeometry(brush, null, Geometry.Parse(IconSpec.PiePath(plan.PieRadius)));
    }

    private static void DrawDot(DrawingContext dc, FramePlan plan)
    {
        dc.DrawEllipse(new SolidColorBrush(IconSpec.Dot), null, plan.DotCenter, plan.DotRadius, plan.DotRadius);
    }

    private static byte[] ToStraightBgra(RenderTargetBitmap rtb)
    {
        int w = rtb.PixelWidth, h = rtb.PixelHeight;
        var pbgra = new byte[w * h * 4];
        rtb.CopyPixels(pbgra, w * 4, 0);

        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte b = pbgra[i * 4], g = pbgra[i * 4 + 1], r = pbgra[i * 4 + 2], a = pbgra[i * 4 + 3];
            if (a > 0)
            {
                // Pbgra32 is premultiplied; straighten before encoding or AA edges get a dark fringe.
                r = (byte)Math.Min(255, r * 255 / a);
                g = (byte)Math.Min(255, g * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }
            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = a;
        }
        return bgra;
    }
}
