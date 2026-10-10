namespace EyeCtrl.App.Tests.Diagnostics;

using System;
using EyeCtrl.Core.Models;

/// <summary>
/// The raster the mirrored-block estimator measures on: BGRA32 pixels plus the frame size.
///
/// <para>
/// R1 (<see cref="ProbeRingRasterTests"/>) builds one straight from a <c>RenderTargetBitmap</c>,
/// whose alpha channel is the ring's rendered coverage. R2 (the on-screen harness and its headless
/// analysis tests) builds one with <see cref="FromWeights"/> from a screen capture: a GDI
/// <c>CopyFromScreen</c> bitmap carries no meaningful alpha, so the measured channel is a per-pixel
/// distance from the capture's own background instead — the same "how much ring is here" signal,
/// read from the same place in the record.
/// </para>
/// </summary>
internal sealed record ProbeRaster(byte[] Pixels, int Width, int Height)
{
    /// <summary>
    /// Packs one weight per pixel into the alpha channel, the only byte the estimator reads; the
    /// colour bytes are left zero.
    /// </summary>
    /// <param name="weights">One weight per pixel, row-major.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    internal static ProbeRaster FromWeights(byte[] weights, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Raster size must be positive; got {width}x{height}.");
        if (weights.Length != width * height)
            throw new ArgumentException(
                $"Weights length {weights.Length} does not match a {width}x{height} raster.", nameof(weights));

        var pixels = new byte[width * height * 4];
        for (var i = 0; i < weights.Length; i++)
            pixels[i * 4 + 3] = weights[i];
        return new ProbeRaster(pixels, width, height);
    }
}

/// <summary>
/// The mirrored-block centroid estimator both raster tests measure with — extracted from R1
/// (<see cref="ProbeRingRasterTests"/>) so R2 reuses it rather than forking it.
///
/// <para>
/// The ring's movement between two frames is estimated from the alpha centroid of the left and
/// right stroke blocks (and the top/bottom pair), each over a ±<see cref="EdgeWindowPixels"/>
/// window centred on the reference edge, then averaged per axis. The average is deliberate: a wide
/// band's <i>outer</i> edge is two coincident antialiased bands composited per-pixel, which makes a
/// single edge's centroid move sub-linearly under a fractional translation (measured up to 0.08 px
/// error on WeightedSandwich); the mirrored block pair carries equal and opposite biases, so the
/// mean cancels them.
/// </para>
///
/// <para>
/// <see cref="ScanRow"/> and <see cref="ScanColumn"/> keep R1's fail-fast contract: an empty window
/// would divide by zero, and the thrown message names the window. A caller that must treat a
/// missing ring as data rather than as an exception — R2's positive control — measures the annulus
/// through <see cref="ProbeRingCaptureAnalysis"/> instead of aborting the scan.
/// </para>
/// </summary>
internal static class ProbeRasterEstimator
{
    /// <summary>Half the scan window, in physical pixels, around the expected cardinal edge.</summary>
    internal const double EdgeWindowPixels = 20.0;

    /// <summary>
    /// The alpha centroid along one row, over a window around a cardinal edge. Measured in device
    /// pixels; for a single antialiased edge this moves exactly with the edge. Throws — naming the
    /// window — when the window holds no weight, which would otherwise divide by zero.
    /// </summary>
    internal static double ScanRow(ProbeRaster raster, int row, double windowCentreX)
    {
        var (from, to, weight, weighted) = AccumulateRow(raster, row, windowCentreX);
        Check.WithCustomMessage(
                $"No ring alpha in the scan window — row {row}, x in [{from}, {to}]. The measurement would divide by zero.")
            .That(weight).IsStrictlyGreaterThan(0.0);
        return weighted / weight;
    }

    /// <summary>The column twin of <see cref="ScanRow"/>.</summary>
    internal static double ScanColumn(ProbeRaster raster, int column, double windowCentreY)
    {
        var (from, to, weight, weighted) = AccumulateColumn(raster, column, windowCentreY);
        Check.WithCustomMessage(
                $"No ring alpha in the scan window — column {column}, y in [{from}, {to}]. The measurement would divide by zero.")
            .That(weight).IsStrictlyGreaterThan(0.0);
        return weighted / weight;
    }

    /// <summary>The x movement, estimated from the mean of the two mirrored left/right blocks.</summary>
    internal static double ShiftX(ProbeRaster moved, ProbeRaster reference, int row, double referenceCentreX, double half)
    {
        var left = ScanRow(moved, row, referenceCentreX - half) - ScanRow(reference, row, referenceCentreX - half);
        var right = ScanRow(moved, row, referenceCentreX + half) - ScanRow(reference, row, referenceCentreX + half);
        return (left + right) / 2.0;
    }

    /// <summary>The y movement, estimated from the mean of the two mirrored top/bottom blocks.</summary>
    internal static double ShiftY(ProbeRaster moved, ProbeRaster reference, int column, double referenceCentreY, double half)
    {
        var top = ScanColumn(moved, column, referenceCentreY - half) - ScanColumn(reference, column, referenceCentreY - half);
        var bottom = ScanColumn(moved, column, referenceCentreY + half) - ScanColumn(reference, column, referenceCentreY + half);
        return (top + bottom) / 2.0;
    }

    /// <summary>The ring's movement as the mean of the mirrored block shifts on both axes.</summary>
    internal static (double X, double Y) Shift(
        ProbeRaster moved, ProbeRaster reference, int row, int column, PhysicalPoint referenceCentre, double half) =>
        (ShiftX(moved, reference, row, referenceCentre.X, half),
         ShiftY(moved, reference, column, referenceCentre.Y, half));

    /// <summary>Rounds a physical coordinate to the whole pixel R1's scan windows are anchored on.</summary>
    internal static int RoundToWholePixel(double physical) =>
        (int)Math.Round(physical, MidpointRounding.AwayFromZero);

    private static (int From, int To, double Weight, double Weighted) AccumulateRow(
        ProbeRaster raster, int row, double windowCentreX)
    {
        var from = Math.Max((int)Math.Floor(windowCentreX - EdgeWindowPixels), 0);
        var to = Math.Min((int)Math.Ceiling(windowCentreX + EdgeWindowPixels), raster.Width - 1);
        var weight = 0.0;
        var weighted = 0.0;
        for (var x = from; x <= to; x++)
        {
            var alpha = raster.Pixels[(row * raster.Width + x) * 4 + 3];
            weight += alpha;
            weighted += alpha * x;
        }

        return (from, to, weight, weighted);
    }

    private static (int From, int To, double Weight, double Weighted) AccumulateColumn(
        ProbeRaster raster, int column, double windowCentreY)
    {
        var from = Math.Max((int)Math.Floor(windowCentreY - EdgeWindowPixels), 0);
        var to = Math.Min((int)Math.Ceiling(windowCentreY + EdgeWindowPixels), raster.Height - 1);
        var weight = 0.0;
        var weighted = 0.0;
        for (var y = from; y <= to; y++)
        {
            var alpha = raster.Pixels[(y * raster.Width + column) * 4 + 3];
            weight += alpha;
            weighted += alpha * y;
        }

        return (from, to, weight, weighted);
    }
}
