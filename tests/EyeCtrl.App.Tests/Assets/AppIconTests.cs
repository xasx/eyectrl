namespace EyeCtrl.App.Tests.Assets;

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Acceptance harness for the regenerated eyectrl.ico — spec A2 (container layout),
/// A3 (splash frame pin), A4a (explicit-size frame resolution + pixel probes).
/// Headless: no window, no app launch; the pack URI needs the WpfApplicationGate.
/// </summary>
public class AppIconTests
{
    // Assembly-qualified: bare "pack://application:,,,/Assets/..." resolves against the
    // RUNNING assembly (the testhost), where the resource does not exist. The component
    // form addresses EyeCtrl.App's embedded resource from anywhere.
    private const string PackUri = "pack://application:,,,/EyeCtrl.App;component/Assets/eyectrl.ico";

    private static byte[] ReadIconResource()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var resource = System.Windows.Application.GetResourceStream(new Uri(PackUri));
        Check.That(resource).IsNotNull();
        using var ms = new MemoryStream();
        resource!.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    // A2 — 6 entries ascending 16/32/48/64/128/256; BMP 16-128 (biHeight = 2h, 32bpp, BI_RGB),
    // PNG 256; all entries planes 1 / bitcount 32 / colour count 0; payloads contiguous.
    [StaFact]
    public void Ico_HasSixAscendingFrames_WithSpecifiedEncoding()
    {
        var data = ReadIconResource();
        Check.That(data.Length).IsStrictlyGreaterThan(102);
        Check.That((int)BitConverter.ToUInt16(data, 0)).IsEqualTo(0);   // reserved
        Check.That((int)BitConverter.ToUInt16(data, 2)).IsEqualTo(1);   // type icon
        var count = BitConverter.ToUInt16(data, 4);
        Check.That((int)count).IsEqualTo(6);

        int[] expected = [16, 32, 48, 64, 128, 256];
        int previousEnd = 6 + 16 * count;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            int w = data[entry];
            int h = data[entry + 1];
            int colorCount = data[entry + 2];
            int reserved = data[entry + 3];
            int planes = BitConverter.ToUInt16(data, entry + 4);
            int bitCount = BitConverter.ToUInt16(data, entry + 6);
            int bytesInRes = (int)BitConverter.ToUInt32(data, entry + 8);
            int imageOffset = (int)BitConverter.ToUInt32(data, entry + 12);
            int size = w == 0 ? 256 : w;

            Check.That(size).IsEqualTo(expected[i]);
            Check.That(h).IsEqualTo(w);                       // 256 encodes as 0 on both axes
            Check.That(colorCount).IsEqualTo(0);
            Check.That(reserved).IsEqualTo(0);
            Check.That(planes).IsEqualTo(1);
            Check.That(bitCount).IsEqualTo(32);
            Check.That(imageOffset).IsEqualTo(previousEnd);

            var payload = data[imageOffset..(imageOffset + bytesInRes)];
            if (size == 256)
            {
                Check.That(payload[..4]).IsEqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG sig
            }
            else
            {
                Check.That(BitConverter.ToInt32(payload, 0)).IsEqualTo(40);                // biSize
                Check.That(BitConverter.ToInt32(payload, 8)).IsEqualTo(size * 2);          // biHeight
                Check.That((int)BitConverter.ToUInt16(payload, 14)).IsEqualTo(32);         // biBitCount
                Check.That(BitConverter.ToUInt32(payload, 16)).IsEqualTo(0u);              // BI_RGB
            }
            previousEnd = imageOffset + bytesInRes;
        }
        Check.That(previousEnd).IsEqualTo(data.Length);       // no trailing bytes
    }

    // A3 — WPF shows WIC's first-enumerated frame; with the BMP-16..128 + PNG-256 layout that
    // is the 128 px frame (r2-measured). SplashWindow's XAML string goes through the same
    // ImageSourceConverter path, so converting the full pack URI string mirrors production.
    [StaFact]
    public void Splash_Decode_Yields128PxFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var source = (ImageSource)new ImageSourceConverter().ConvertFrom(PackUri)!;
        Check.That(((BitmapSource)source).PixelWidth).IsEqualTo(128);
    }

    // A4a — explicit sizes only: SmallIconSize keys would break on CI (16 at 96 DPI).
    // Probes are colour sanity at the blank-slate design's structural points (tile / amber
    // gaze dot / cyan dwell arc / warm bloom); A2 is the structural guarantee for
    // "which frame is in the file". Sample-centre math: 32 px = 96-grid / 3.
    [StaFact]
    public void Ico_ExplicitSizeRequest_ReturnsMatchingFrames()
    {
        var data = ReadIconResource();

        using var stream32 = new MemoryStream(data);
        using var icon32 = new System.Drawing.Icon(stream32, new System.Drawing.Size(32, 32));
        Check.That(icon32.Width).IsEqualTo(32);
        Check.That(icon32.Height).IsEqualTo(32);
        using var bmp32 = icon32.ToBitmap();
        ProbeDarkTile(bmp32, 4, 4);       // (13.5,13.5)@96 — corner, clear of bloom/trail/arc
        ProbeAmberDot(bmp32, 16, 17);     // (49.5,52.5)@96 — inside the gaze point
        ProbeCyanArc(bmp32, 28, 17);      // (85.5,52.5)@96 — on the arc at 3 o'clock
        // No bloom probe at 32/16: the lift is ~5 R-points inside the dot's AA fringe —
        // GDI+ rounding swamps it. The bloom is probed at 128 px below, where its band is wide.

        using var stream16 = new MemoryStream(data);
        using var icon16 = new System.Drawing.Icon(stream16, new System.Drawing.Size(16, 16));
        Check.That(icon16.Width).IsEqualTo(16);
        Check.That(icon16.Height).IsEqualTo(16);
        using var bmp16 = icon16.ToBitmap();
        ProbeDarkTile(bmp16, 2, 2);       // (15,15)@96
        ProbeAmberDot(bmp16, 8, 8);       // (51,51)@96

        using var stream128 = new MemoryStream(data);
        using var icon128 = new System.Drawing.Icon(stream128, new System.Drawing.Size(128, 128));
        Check.That(icon128.Width).IsEqualTo(128);
        using var bmp128 = icon128.ToBitmap();
        ProbeWarmBloom(bmp128, 38, 96);   // (28.5,72)@96 — bloom tail at 135°, clean gradient
    }

    // A4b — pin the production call shape: the loader must request an explicit size
    // (the ctor passes SystemInformation.SmallIconSize), never the bare Icon(Stream)
    // that picks the large frame (64 px at 200 % DPI). TrayIconManager cannot be
    // constructed in a test (its ctor makes a visible NotifyIcon), so the extracted
    // static is the pin target.
    [StaFact]
    public void LoadTrayIcon_ExplicitSize_ResolvesRequestedFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var resource = System.Windows.Application.GetResourceStream(new Uri(PackUri));
        Check.That(resource).IsNotNull();
        using var icon = EyeCtrl.App.Services.TrayIconManager.LoadTrayIcon(
            resource!.Stream, new System.Drawing.Size(32, 32));
        Check.That(icon.Width).IsEqualTo(32);
        Check.That(icon.Height).IsEqualTo(32);
    }

    private static void ProbeDarkTile(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyLessThan(40);
        Check.That((int)c.G).IsStrictlyLessThan(40);
        Check.That((int)c.B).IsStrictlyLessThan(56);
    }

    private static void ProbeAmberDot(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsGreaterOrEqualThan(205);
        Check.That((int)c.G).IsGreaterOrEqualThan(135);
        Check.That((int)c.G).IsStrictlyLessThan(190);
        Check.That((int)c.B).IsStrictlyLessThan(90);
    }

    private static void ProbeCyanArc(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.G).IsStrictlyGreaterThan(180);
        Check.That((int)c.B).IsStrictlyGreaterThan(185);
        Check.That((int)c.R).IsStrictlyLessThan(180);
    }

    private static void ProbeWarmBloom(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsGreaterOrEqualThan(30);
        Check.That((int)c.R).IsStrictlyLessThan(85);
        Check.That((int)c.G).IsGreaterOrEqualThan(26);
        Check.That((int)c.G).IsStrictlyLessThan(70);
        Check.That((int)c.B).IsStrictlyLessThan(55);
        Check.That((int)c.R).IsStrictlyGreaterThan((int)c.G);  // warm, not grey
    }
}
