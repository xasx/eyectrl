namespace EyeCtrl.App;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using TextRenderer = System.Windows.Forms.TextRenderer;
using TextFormatFlags = System.Windows.Forms.TextFormatFlags;

/// <summary>
/// Renders emoji glyphs from Segoe UI Emoji via GDI (TextRenderer) using a
/// screen-compatible DC to produce full-color BitmapSource images for WPF.
///
/// GDI's COLR color-font rendering path (Segoe UI Emoji on Windows 10+) is only
/// activated when drawing to a screen-compatible device context.  A GDI+
/// DIB-backed memory DC (Graphics.FromImage) does NOT trigger COLR and produces
/// monochrome output.  We therefore create the DC with:
///   CreateCompatibleDC(GetDC(NULL)) + CreateCompatibleBitmap(screenDC, w, h)
/// which gives GDI a real screen-compatible device-dependent bitmap and unlocks
/// the full COLR rendering pipeline.
/// </summary>
internal sealed class EmojiRenderService : IDisposable
{
    private static readonly Color TileBackground = Color.FromArgb(50, 50, 50);

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = (emoji, pixelSize);
        if (!_cache.TryGetValue(key, out var src))
            _cache[key] = src = Render(emoji, pixelSize);
        return src;
    }

    private static BitmapSource Render(string emoji, int pixelSize)
    {
        try
        {
            using var font = new Font("Segoe UI Emoji", pixelSize * 0.75f, GraphicsUnit.Pixel);

            // Acquire a screen DC and create a compatible memory DC + bitmap.
            // A device-dependent bitmap (DDB) created via CreateCompatibleBitmap(screenDC)
            // is what activates GDI's COLR color-font code path for Segoe UI Emoji.
            var screenDC = GetDC(IntPtr.Zero);
            var memDC    = CreateCompatibleDC(screenDC);
            var hBitmap  = CreateCompatibleBitmap(screenDC, pixelSize, pixelSize);
            var oldObj   = SelectObject(memDC, hBitmap);

            try
            {
                // Fill background and render emoji via GDI on the screen-compatible DC.
                // Graphics.FromHdc wraps memDC so GDI+ Clear() targets the same surface;
                // TextRenderer.DrawText calls GetHdc/ReleaseHdc internally to invoke GDI.
                using (var g = Graphics.FromHdc(memDC))
                {
                    g.Clear(TileBackground);
                    TextRenderer.DrawText(g, emoji, font,
                        new Rectangle(0, 0, pixelSize, pixelSize),
                        foreColor: Color.White,
                        backColor: TileBackground,
                        flags: TextFormatFlags.HorizontalCenter
                             | TextFormatFlags.VerticalCenter
                             | TextFormatFlags.NoPrefix
                             | TextFormatFlags.PreserveGraphicsClipping);
                }

                // Extract pixels from the DDB as a top-down 32-bpp BGRA DIB.
                var bmi = new BITMAPINFOHEADER
                {
                    biSize        = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth       = pixelSize,
                    biHeight      = -pixelSize, // negative ⇒ top-down scan order
                    biPlanes      = 1,
                    biBitCount    = 32,
                    biCompression = BI_RGB,
                };
                int byteCount = pixelSize * pixelSize * 4;
                var pixels = new byte[byteCount];
                GetDIBits(screenDC, hBitmap, 0, (uint)pixelSize, pixels, ref bmi, DIB_RGB_COLORS);

                // GDI COLR renders correct RGB but writes alpha = 0 — make every pixel opaque.
                for (int i = 3; i < byteCount; i += 4)
                    pixels[i] = 255;

                var result = BitmapSource.Create(
                    pixelSize, pixelSize, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null,
                    pixels, pixelSize * 4);
                result.Freeze();
                return result;
            }
            finally
            {
                SelectObject(memDC, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }
        catch
        {
            // A single unrenderable emoji must not crash BuildEmojiGrid.
            var placeholder = BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
            placeholder.Freeze();
            return placeholder;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }

    // ── GDI P/Invoke ─────────────────────────────────────────────────────────

    private const uint BI_RGB         = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint   biSize;
        public int    biWidth;
        public int    biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint   biCompression;
        public uint   biSizeImage;
        public int    biXPelsPerMeter;
        public int    biYPelsPerMeter;
        public uint   biClrUsed;
        public uint   biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap,
        uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint uUsage);
}
