namespace EyeCtrl.App.Tests.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// G-A render guard: the investigation measured the big view at exactly 2.000 displayed px per
/// captured px (the configured magnification) with an opaque disc. That measurement lived only in a
/// throwaway probe, so nothing stopped a geometry or XAML change from silently reintroducing
/// "it isn't zoomed".
///
/// <para>
/// The probe is now permanent and headless: a synthetic 10-px black/white checker built through the
/// production bitmap path (<c>Bitmap</c> → <c>GetHbitmap</c> → <c>CreateBitmapSourceFromHBitmap</c>)
/// is assigned to the real window, the window is laid out WITHOUT being shown, and its content is
/// rendered by <see cref="RenderTargetBitmap"/> at <c>96 × scale</c>. The mean checker run in device
/// pixels divided by the 10-px square is the view's scale; a central sample proves the disc is
/// opaque (not the see-through glass the owner saw).
/// </para>
///
/// <para>
/// No screen pixel is read, no window is shown and no input is touched.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionRenderScaleTests
{
    private const int RadiusDip = 170;
    private const double Magnification = 2.0;
    private const int CheckerSquarePx = 10;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void RenderedMagnifier_ShowsTwoDevicePixelsPerCapturedPixel_AndAnOpaqueDisc(double scale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(scale, scale);
        var window = new ManualSelectionOverlayWindow();
        try
        {
            var captureSide = 2 * (int)(RadiusDip * DpiHelper.ScaleX);
            window.ManualSelectionBrush.ImageSource = Checker(captureSide, CheckerSquarePx);
            // A picture is already present, so UpdateCenter does not read the screen.
            window.UpdateCenter(new PhysicalPoint(1000, 1000), RadiusDip, Magnification, recapture: false);

            var rendered = Render(window, scale);

            var pxPerCapturedPx = MeasureCheckerRun(rendered);
            Check.WithCustomMessage(
                    $"the magnifier rendered {pxPerCapturedPx:0.###} px per captured px at display scale "
                    + $"{scale}; the view must show the configured {Magnification:0.#}× zoom.")
                .That(pxPerCapturedPx).IsCloseTo(Magnification, 0.05);

            var alpha = MeanCentralAlpha(rendered);
            Check.WithCustomMessage($"the disc's central alpha is {alpha:0.#}; it must be opaque (255).")
                .That(alpha).IsCloseTo(255, 0.5);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            window.Close();
        }
    }

    /// <summary>
    /// F5 (fixup 01): with no picture the disc used to be fully transparent, showing the
    /// backdrop-dimmed 1× screen through it while dwells still mapped as magnified. The theme panel
    /// under-fill makes it opaque; the surface token carries the theme's 0xFA alpha, so "opaque"
    /// means the central area is at the theme surface's alpha (≥250), never see-through.
    /// </summary>
    [StaFact]
    public void AMissingPicture_LeavesAnOpaqueDisc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            window.Capture = (_, _) => null; // the failed-capture state: picture=none
            window.UpdateCenter(new PhysicalPoint(1000, 1000), RadiusDip, Magnification);

            var rendered = Render(window, 1.0);
            var alpha = MeanCentralAlpha(rendered);

            Check.WithCustomMessage(
                    $"with no picture the disc's central alpha is {alpha:0.#}; it must be opaque "
                    + "(the theme panel surface's 0xFA), so the normal-size screen does not show through.")
                .That(alpha).IsCloseTo(255, 5);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Renders the window's content at <c>96 × scale</c> without ever showing the window: measure and
    /// arrange the content at the window's DIP size, then rasterize it.
    /// </summary>
    private static RenderTargetBitmap Render(Window window, double scale)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();

        var target = new RenderTargetBitmap(
            (int)Math.Round(window.Width * scale),
            (int)Math.Round(window.Height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        target.Render(content);
        return target;
    }

    /// <summary>The mean run length (device px) of one checker colour along the centre row, / square size.</summary>
    private static double MeasureCheckerRun(BitmapSource rendered)
    {
        var (pixels, stride) = ReadPixels(rendered);
        var row = rendered.PixelHeight / 2;
        var startX = (int)(rendered.PixelWidth * 0.2);
        var endX = (int)(rendered.PixelWidth * 0.8);

        var runs = new List<int>();
        int currentRun = 0;
        bool? currentWhite = null;
        for (var x = startX; x < endX; x++)
        {
            var offset = row * stride + x * 4;
            var luma = (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
            var isWhite = luma > 128;
            if (isWhite == currentWhite)
            {
                currentRun++;
            }
            else
            {
                if (currentWhite is not null) runs.Add(currentRun);
                currentWhite = isWhite;
                currentRun = 1;
            }
        }

        // The first run is a partial checker square (the row starts mid-square): discard it, as the
        // investigation's probe did.
        if (runs.Count == 0) return 0;
        runs.RemoveAt(0);
        return runs.Count == 0 ? 0 : runs.Average() / CheckerSquarePx;
    }

    /// <summary>Mean alpha of the central 40 % square — well inside the disc, away from the rim.</summary>
    private static double MeanCentralAlpha(BitmapSource rendered)
    {
        var (pixels, stride) = ReadPixels(rendered);
        var x0 = (int)(rendered.PixelWidth * 0.3);
        var x1 = (int)(rendered.PixelWidth * 0.7);
        var y0 = (int)(rendered.PixelHeight * 0.3);
        var y1 = (int)(rendered.PixelHeight * 0.7);

        long sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                sum += pixels[y * stride + x * 4 + 3];
                count++;
            }
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    private static (byte[] Pixels, int Stride) ReadPixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }

    /// <summary>
    /// A black/white checker built through the production capture path — a GDI bitmap to an
    /// <see cref="InteropBitmap"/>-shaped source — so the gate exercises the same pixel pipeline the
    /// screen capture feeds.
    /// </summary>
    private static BitmapSource Checker(int side, int square)
    {
        using var bitmap = new System.Drawing.Bitmap(side, side);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var black = new System.Drawing.SolidBrush(System.Drawing.Color.Black))
        using (var white = new System.Drawing.SolidBrush(System.Drawing.Color.White))
        {
            for (var y = 0; y < side; y += square)
            {
                for (var x = 0; x < side; x += square)
                {
                    var brush = (x / square + y / square) % 2 == 0 ? black : white;
                    graphics.FillRectangle(brush, x, y, square, square);
                }
            }
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
