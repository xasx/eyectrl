namespace EyeCtrl.App;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using TextRenderer = System.Windows.Forms.TextRenderer;
using TextFormatFlags = System.Windows.Forms.TextFormatFlags;

/// <summary>
/// Renders emoji glyphs from Segoe UI Emoji via GDI (TextRenderer) to produce
/// full-color BitmapSource images for use in WPF Image controls.
///
/// WPF TextBlock does not render COLR color layers from Segoe UI Emoji.
/// GDI (via TextRenderer) does — but writes alpha=0 for all pixels.
/// This service fixes the alpha channel and caches results per (emoji, size).
/// </summary>
internal sealed class EmojiRenderService : IDisposable
{
    private readonly Dictionary<(string emoji, int size), BitmapSource> _cache = new();
    private bool _disposed;

    public bool IsAvailable { get; private init; }

    public EmojiRenderService()
    {
        try
        {
            // Probe font availability
            using var probe = new Font("Segoe UI Emoji", 10, GraphicsUnit.Pixel);
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>Returns a cached, frozen BitmapSource for the given emoji at the given pixel size.</summary>
    public BitmapSource Get(string emoji, int pixelSize)
    {
        var key = (emoji, pixelSize);
        if (!_cache.TryGetValue(key, out var src))
            _cache[key] = src = Render(emoji, pixelSize);
        return src;
    }

    private BitmapSource Render(string emoji, int pixelSize)
    {
        // Font size: ~75% of bitmap size leaves a small margin
        using var font = new Font("Segoe UI Emoji", pixelSize * 0.75f, GraphicsUnit.Pixel);

        // Render on a solid dark background — GDI writes alpha=0 for all pixels,
        // so we make the whole bitmap fully opaque after rendering.
        using var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // Fill with the standard tile background color (matches Border background)
            g.Clear(Color.FromArgb(50, 50, 50));

            var rect = new Rectangle(0, 0, pixelSize, pixelSize);
            TextRenderer.DrawText(g, emoji, font, rect,
                foreColor: Color.White,
                backColor: Color.FromArgb(50, 50, 50),
                flags: TextFormatFlags.HorizontalCenter
                     | TextFormatFlags.VerticalCenter
                     | TextFormatFlags.NoPrefix
                     | TextFormatFlags.PreserveGraphicsClipping);
        }

        // Fix GDI alpha channel: GDI renders correct RGB but sets alpha=0 for all pixels.
        // Set alpha=255 for every pixel so the bitmap is fully opaque.
        FixAlphaChannel(bitmap, pixelSize);

        // Convert to WPF BitmapSource via LockBits (preserves all channels correctly)
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, pixelSize, pixelSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        BitmapSource result;
        try
        {
            result = BitmapSource.Create(
                pixelSize, pixelSize,
                dpiX: 96, dpiY: 96,
                pixelFormat: System.Windows.Media.PixelFormats.Bgra32,
                palette: null,
                buffer: bitmapData.Scan0,
                bufferSize: Math.Abs(bitmapData.Stride) * pixelSize,
                stride: bitmapData.Stride);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        result.Freeze(); // required for cross-thread use and performance
        return result;
    }

    private static void FixAlphaChannel(Bitmap bitmap, int pixelSize)
    {
        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, pixelSize, pixelSize),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = Math.Abs(bmpData.Stride) * pixelSize;
            var bytes = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, bytes, 0, byteCount);

            // BGRA layout: alpha is at offset 3 of each 4-byte pixel
            for (int i = 3; i < byteCount; i += 4)
                bytes[i] = 255;

            Marshal.Copy(bytes, 0, bmpData.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }
}
