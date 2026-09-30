namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class FrameRenderer
{
    /// <summary>Renders one frame to straight-alpha BGRA pixels, top-down rows.</summary>
    public static byte[] RenderBgra(FramePlan plan)
    {
        var scale = plan.Size / IconSpec.GridSize;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawTile(dc);
            // Scale the whole composition about the tile centre (IconSpec.ContentScale) —
            // geometry constants stay authored in comfortable 96-grid margins. The tile
            // itself stays full-size (scaling it would bleed past the canvas).
            // TransformGroup composes children LAST-ADDED-FIRST: to map p -> 48 + k(p-48),
            // the outer translate must be added LAST (measured: the other order shifted the
            // whole composition +16.8 units and blew it past the tile).
            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    new TranslateTransform(-48, -48),
                    new ScaleTransform(IconSpec.ContentScale, IconSpec.ContentScale),
                    new TranslateTransform(48, 48),
                },
            });
            DrawBloom(dc);
            DrawTrail(dc);
            DrawArc(dc);
            DrawDot(dc);
            dc.Pop();
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(plan.Size, plan.Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var pixels = ToStraightBgra(rtb);
        GC.KeepAlive(visual);
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

    private static void DrawTile(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(IconSpec.Tile), null,
            new Rect(IconSpec.TileX, IconSpec.TileY, IconSpec.TileExtent, IconSpec.TileExtent),
            IconSpec.TileRadius, IconSpec.TileRadius);
    }

    private static void DrawBloom(DrawingContext dc)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        foreach (var (offset, alpha) in IconSpec.BloomStops)
            brush.GradientStops.Add(new GradientStop(WithAlpha(IconSpec.Amber, alpha), offset));
        dc.DrawEllipse(brush, null, IconSpec.DotCenter, IconSpec.BloomRadius, IconSpec.BloomRadius);
    }

    private static void DrawTrail(DrawingContext dc)
    {
        dc.DrawEllipse(WithAlphaBrush(IconSpec.Amber, IconSpec.TrailOuterAlpha), null,
            IconSpec.TrailOuter, IconSpec.TrailOuterRadius, IconSpec.TrailOuterRadius);
        dc.DrawEllipse(WithAlphaBrush(IconSpec.Amber, IconSpec.TrailInnerAlpha), null,
            IconSpec.TrailInner, IconSpec.TrailInnerRadius, IconSpec.TrailInnerRadius);
    }

    private static void DrawArc(DrawingContext dc)
    {
        // StreamGeometry rather than path strings: no double-formatting, so no locale traps.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(IconSpec.PointOnArc(IconSpec.ArcStartDegrees), false, false);
            ctx.ArcTo(
                IconSpec.PointOnArc(IconSpec.ArcStartDegrees + IconSpec.ArcSweepDegrees),
                new Size(IconSpec.ArcRadius, IconSpec.ArcRadius),
                0, IconSpec.ArcSweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        var pen = new Pen(WithAlphaBrush(IconSpec.Cyan, IconSpec.ArcAlpha), IconSpec.ArcWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawDot(DrawingContext dc)
    {
        dc.DrawEllipse(new SolidColorBrush(IconSpec.Amber), null,
            IconSpec.DotCenter, IconSpec.DotRadius, IconSpec.DotRadius);
    }

    private static SolidColorBrush WithAlphaBrush(System.Windows.Media.Color color, double alpha) =>
        new(WithAlpha(color, alpha));

    private static System.Windows.Media.Color WithAlpha(System.Windows.Media.Color color, double alpha) =>
        System.Windows.Media.Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B);

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
