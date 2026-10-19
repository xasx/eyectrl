namespace EyeCtrl.App.Services;

using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Statistics over a capture for the magnifier's field log: luma mean and standard deviation (is
/// the picture plausible, or flat/black?) and a coarse signature (every 16th pixel) used for the
/// cheap "is this the same as the previous capture?" check.
///
/// <para>
/// Statistics only: no pixel is ever logged or retained. The signature reveals a STALE picture in
/// the field — two consecutive sessions on the same target hash the same — without recording what
/// was on screen.
/// </para>
/// </summary>
internal static class MagnifierCaptureStats
{
    private const int SignaturePixelStride = 16;

    /// <summary>Luma mean and standard deviation (0–255) plus the sampled signature.</summary>
    internal static (double Mean, double StandardDeviation, ulong Signature) Analyze(BitmapSource image)
    {
        var gray = image.Format == PixelFormats.Gray8
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);

        var stride = (gray.PixelWidth * gray.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * gray.PixelHeight];
        gray.CopyPixels(pixels, stride, 0);

        long sum = 0;
        long sumOfSquares = 0;
        var count = 0;
        // FNV-1a over every 16th pixel: cheap, order-sensitive, and stable for a given image.
        var signature = 14695981039346656037UL;

        for (var y = 0; y < gray.PixelHeight; y++)
        {
            var rowStart = y * stride;
            var pixelIndex = y * gray.PixelWidth;
            for (var x = 0; x < gray.PixelWidth; x++, pixelIndex++)
            {
                var luma = pixels[rowStart + x];
                sum += luma;
                sumOfSquares += (long)luma * luma;
                count++;

                if (pixelIndex % SignaturePixelStride == 0)
                {
                    signature ^= luma;
                    signature *= 1099511628211UL;
                }
            }
        }

        if (count == 0) return (0, 0, signature);

        var mean = (double)sum / count;
        // Population variance; a capture is the whole population, not a sample of one.
        var variance = (double)sumOfSquares / count - mean * mean;
        return (mean, Math.Sqrt(Math.Max(0, variance)), signature);
    }
}
