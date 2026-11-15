namespace EyeCtrl.Platform.Icons;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// The neutral default glyph of the icon decision (S3-B source 4): a drawn application-window
/// outline — no asset, no theme resource — returned whenever no real source could produce an icon,
/// so a tile always shows a purpose-built placeholder instead of an empty hole or another app's
/// icon.
///
/// <para>
/// The stroke is a theme-INDEPENDENT mid grey: the glyph is created in <c>EyeCtrl.Platform</c>,
/// which cannot see the theme tokens, and it must read on the switcher's Midnight surface
/// (#2A2A44, ≈ 4.4:1 against it) as well as on Daylight's (#F3F4F9, ≈ 3.4:1) — a single frozen
/// drawing cannot follow two themes, so it takes the one colour that works on both.
/// </para>
/// </summary>
internal sealed class DefaultGlyphIconSource : IAppIconSource
{
    /// <summary>The 24 × 24 DIP drawn glyph (frozen; the tile's Image scales it to 40 DIP).</summary>
    internal static readonly ImageSource Glyph = CreateGlyph();

    public AppIconSourceKind Kind => AppIconSourceKind.Default;

    public ImageSource? TryGetIcon(IconRequest request) => Glyph;

    private static ImageSource CreateGlyph()
    {
        // The frame and its title-bar line: the same shape language as the desktop tile's drawn
        // monitor, at a 24 DIP viewbox so the 40 DIP draw is a clean 1.67x scale.
        var frame = new RectangleGeometry(new Rect(3, 5.5, 18, 13), 1.5, 1.5);
        var titleBar = new LineGeometry(new Point(3, 9.5), new Point(21, 9.5));
        var shapes = new GeometryGroup();
        shapes.Children.Add(frame);
        shapes.Children.Add(titleBar);
        shapes.Freeze();

        var stroke = new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C));
        stroke.Freeze();
        var pen = new Pen(stroke, 1.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new GeometryDrawing(null, pen, shapes);
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
