namespace EyeCtrl.App.Tests.Fixtures;

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// A deterministic screen stand-in for the capture seam: every pixel encodes the screen coordinate
/// it would have come from. No screen pixel is ever read.
///
/// <para>
/// Encoding, per physical pixel (x, y): R = x &amp; 0xFF, G = y &amp; 0xFF,
/// B = ((x >> 8) &amp; 0x0F) | (((y >> 8) &amp; 0x0F) &lt;&lt; 4), A = 255. The screen is
/// 3840×2400, so four high bits per axis are enough.
/// </para>
/// </summary>
internal static class SyntheticScreenGrab
{
    internal static Bitmap Grab(int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var screenX = x + i;
                var screenY = y + j;
                var offset = j * stride + i * 4;
                pixels[offset] = (byte)(((screenX >> 8) & 0x0F) | (((screenY >> 8) & 0x0F) << 4)); // B
                pixels[offset + 1] = (byte)(screenY & 0xFF);                                       // G
                pixels[offset + 2] = (byte)(screenX & 0xFF);                                       // R
                pixels[offset + 3] = 255;                                                          // A
            }
        }

        var bits = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        return bitmap;
    }

    /// <summary>Decodes a Bgra32 pixel back to the screen coordinate it encodes.</summary>
    internal static (int X, int Y) Decode(byte[] bgra, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        var blue = bgra[offset];
        var green = bgra[offset + 1];
        var red = bgra[offset + 2];
        return (((blue & 0x0F) << 8) | red, (((blue >> 4) & 0x0F) << 8) | green);
    }

    internal static byte AlphaAt(byte[] bgra, int stride, int x, int y)
        => bgra[y * stride + x * 4 + 3];
}
