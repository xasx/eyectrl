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
    // Probes are colour sanity at structural points (tile/body/pie/dot); A2 is the
    // structural guarantee for "which frame is in the file".
    [StaFact]
    public void Ico_ExplicitSizeRequest_ReturnsTunedSmallFrames()
    {
        var data = ReadIconResource();

        using var stream32 = new MemoryStream(data);
        using var icon32 = new System.Drawing.Icon(stream32, new System.Drawing.Size(32, 32));
        Check.That(icon32.Width).IsEqualTo(32);
        Check.That(icon32.Height).IsEqualTo(32);
        using var bmp32 = icon32.ToBitmap();
        ProbeDarkTile(bmp32, 4, 4);       // (13.5,13.5)@96 — inside the tile, clear of the corner cut
        ProbeBody(bmp32, 10, 15);         // (30,44)@96 almond fill, clear of iris + edges
        ProbeAmberPie(bmp32, 16, 16);     // (48,48)@96 pie apex
        ProbeBrightDot(bmp32, 20, 16);    // (61,48)@96 gaze dot on the pie head

        using var stream16 = new MemoryStream(data);
        using var icon16 = new System.Drawing.Icon(stream16, new System.Drawing.Size(16, 16));
        Check.That(icon16.Width).IsEqualTo(16);
        Check.That(icon16.Height).IsEqualTo(16);
        using var bmp16 = icon16.ToBitmap();
        ProbeDarkTile(bmp16, 3, 3);       // (21,21)@96 — inside the tile, clear of the corner cut
        ProbeAmberPie(bmp16, 8, 8);       // centre = solid #E8A33C pie
        ProbeBrightDot(bmp16, 10, 8);     // (62.5,48)@96 dot
    }

    private static void ProbeDarkTile(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyLessThan(64);
        Check.That((int)c.G).IsStrictlyLessThan(64);
        Check.That((int)c.B).IsStrictlyLessThan(96);
    }

    private static void ProbeBody(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.B).IsStrictlyGreaterThan(205);  // body is the only blue-light region
        Check.That((int)c.G).IsStrictlyGreaterThan(165);
        Check.That((int)c.R).IsStrictlyLessThan(170);
    }

    private static void ProbeAmberPie(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyGreaterThan(200);
        Check.That((int)c.G).IsStrictlyGreaterThan(140);
        Check.That((int)c.B).IsStrictlyLessThan(160);
    }

    private static void ProbeBrightDot(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyGreaterThan(215);
        Check.That((int)c.G).IsStrictlyGreaterThan(225);
        Check.That((int)c.B).IsStrictlyGreaterThan(230);
    }
}
