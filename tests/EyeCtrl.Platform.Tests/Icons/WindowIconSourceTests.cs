namespace EyeCtrl.Platform.Tests.Icons;

using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B source 3 (the window's own icon) and source 4 (the neutral glyph): the last two steps of
/// the decision. The real time bound of the window-message read is proven separately, against a
/// genuinely non-answering window, in <see cref="WindowMessageIconReaderTests"/>.
/// </summary>
public class WindowIconSourceTests
{
    [Fact]
    public void TheReaderIsAskedForTheWindowsOwnHandle()
    {
        var icon = IconFakes.Image(61);
        var reader = new FakeWindowIconReader(icon);
        var source = new WindowIconSource(reader);

        var result = source.TryGetIcon(new IconRequest(new IntPtr(0x3001), 601));

        Check.That(result).IsSameReferenceAs(icon);
        Check.That(reader.LastHandle).IsEqualTo(new IntPtr(0x3001));
        Check.That(reader.Calls).IsEqualTo(1);
    }

    [Fact]
    public void AWindowWithoutAnIcon_Declines()
    {
        var source = new WindowIconSource(new FakeWindowIconReader(null));

        Check.That(source.TryGetIcon(new IconRequest(new IntPtr(0x3002), 602))).IsNull();
    }

    [Fact]
    public void TheDefaultGlyph_IsAFrozenDrawnWindow_NotAnEmptyHole()
    {
        var glyph = DefaultGlyphIconSource.Glyph;

        Check.That(glyph).IsNotNull();
        Check.WithCustomMessage("A cached glyph is shared across threads; it must be frozen.")
            .That(glyph.IsFrozen).IsTrue();

        var drawingImage = glyph as System.Windows.Media.DrawingImage;
        Check.That(drawingImage).IsNotNull();
        var bounds = drawingImage!.Drawing.Bounds;
        Check.WithCustomMessage("The default glyph must draw something (a neutral window outline).")
            .That(bounds.Width).IsStrictlyGreaterThan(0);
        Check.That(bounds.Height).IsStrictlyGreaterThan(0);

        // 24 x 24 viewbox: the tile's Image scales it to the design's 40 DIP.
        Check.That(bounds.Left).IsGreaterOrEqualThan(0);
        Check.That(bounds.Top).IsGreaterOrEqualThan(0);
        Check.That(bounds.Right).IsLessOrEqualThan(24);
        Check.That(bounds.Bottom).IsLessOrEqualThan(24);
    }

    [Fact]
    public void TheDefaultSourceAnswersEveryRequest()
    {
        var source = new DefaultGlyphIconSource();

        Check.That(source.Kind).IsEqualTo(AppIconSourceKind.Default);
        Check.That(source.TryGetIcon(new IconRequest(IntPtr.Zero, 0)))
            .IsSameReferenceAs(DefaultGlyphIconSource.Glyph);
    }
}
