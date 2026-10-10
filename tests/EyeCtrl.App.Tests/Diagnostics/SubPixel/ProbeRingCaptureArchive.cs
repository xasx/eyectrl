namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// One captured frame with everything the archive and the measurement need: what was scheduled,
/// the raw BGRA pixels (for the PNG), the estimator's weight raster, and the measured background.
/// <see cref="Kind"/> separates the reference repeats, the A/B assertion rows and the sweep rows:
/// the assertion fractions 0.25/0.5/0.75 are also sweep fractions, so the fraction alone cannot
/// tell the rows apart.
/// </summary>
/// <param name="Kind">The row kind: see <see cref="ProbeRingOnScreenJudge.KindReference"/> and friends.</param>
/// <param name="Condition">"A" or "B".</param>
/// <param name="Fraction">The scheduled sub-pixel fraction.</param>
/// <param name="Repeat">The capture's index within its row (0-based).</param>
/// <param name="Frame">The scheduled geometry the capture used.</param>
/// <param name="Bgra">The raw capture pixels, BGRA32 row-major.</param>
/// <param name="SizePx">The frame's side in pixels.</param>
/// <param name="Weights">The estimator raster derived from <paramref name="Bgra"/>.</param>
/// <param name="Background">The measured background colour.</param>
internal sealed record ProbeRingCapturedSample(
    string Kind,
    string Condition,
    double Fraction,
    int Repeat,
    ProbeRingFrameSchedule.Frame Frame,
    byte[] Bgra,
    int SizePx,
    ProbeRaster Weights,
    (byte R, byte G, byte B) Background);

/// <summary>The saved run's manifest header (fixup 03 item 5).</summary>
internal sealed record ProbeRingManifest(double Scale, string CreatedUtc, IReadOnlyList<ProbeRingManifestSample> Samples);

/// <summary>One manifest entry: enough to rebuild a capture's frame, background and weight raster.</summary>
internal sealed record ProbeRingManifestSample(
    string File,
    string Kind,
    string Condition,
    double Fraction,
    int Repeat,
    int CaptureOriginX,
    int CaptureOriginY,
    double CaptureCentreX,
    double CaptureCentreY,
    double WindowOriginX,
    double WindowOriginY,
    int SizePx,
    byte BackgroundR,
    byte BackgroundG,
    byte BackgroundB,
    string Utc);

/// <summary>A loaded run: its samples, the scale they were captured at, and when.</summary>
internal sealed record ProbeRingArchive(
    IReadOnlyList<ProbeRingCapturedSample> Samples, double Scale, DateTimeOffset CreatedUtc);

/// <summary>
/// R2's raw-capture archive (fixup 03 item 5): every on-screen capture as a lossless PNG plus a
/// JSON manifest, under <c>%TEMP%\eyectrl-r2-&lt;UTC stamp&gt;\</c> — never under
/// <c>%APPDATA%\EyeCtrl</c>. <see cref="Load"/> reads it back, so all later diagnosis can be rerun
/// offline and headless from the files without another on-screen run.
///
/// <para>
/// The PNG is written from the raw pixels as <see cref="PixelFormats.Bgr32"/>: the capture's alpha
/// byte is meaningless (GDI does not transfer one) and a straight-alpha PNG round-trip could
/// premultiply it. The estimator reads RGB only, so the reloaded weights are bit-identical to the
/// live ones.
/// </para>
/// </summary>
internal static class ProbeRingCaptureArchive
{
    /// <summary>The run folder name prefix; the full name adds the UTC stamp.</summary>
    internal const string FolderPrefix = "eyectrl-r2-";

    /// <summary>The manifest's file name inside a run folder.</summary>
    internal const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The folder one run writes to: <c>%TEMP%\eyectrl-r2-&lt;yyyyMMdd-HHmmss&gt;\</c>.
    /// <c>%TEMP%</c> by construction — never <c>%APPDATA%\EyeCtrl</c>, which SP-6 reads.
    /// </summary>
    /// <param name="utcNow">The run's start, UTC.</param>
    internal static string CreateRunFolder(DateTimeOffset utcNow) =>
        Path.Combine(Path.GetTempPath(),
            FolderPrefix + utcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>Writes every sample as a PNG and the manifest beside them.</summary>
    /// <param name="folder">The run folder from <see cref="CreateRunFolder"/>.</param>
    /// <param name="samples">The captured samples, in capture order.</param>
    /// <param name="scale">The window's effective scale.</param>
    /// <param name="createdUtc">The run's start, UTC.</param>
    internal static void Save(
        string folder, IReadOnlyList<ProbeRingCapturedSample> samples, double scale, DateTimeOffset createdUtc)
    {
        Directory.CreateDirectory(folder);

        var entries = new List<ProbeRingManifestSample>(samples.Count);
        foreach (var sample in samples)
        {
            var file = SampleFileName(sample);
            WritePng(Path.Combine(folder, file), sample);
            entries.Add(new ProbeRingManifestSample(
                file,
                sample.Kind,
                sample.Condition,
                sample.Fraction,
                sample.Repeat,
                sample.Frame.CaptureOriginX,
                sample.Frame.CaptureOriginY,
                sample.Frame.CaptureCentrePx.X,
                sample.Frame.CaptureCentrePx.Y,
                sample.Frame.WindowOriginPx.X,
                sample.Frame.WindowOriginPx.Y,
                sample.SizePx,
                sample.Background.R,
                sample.Background.G,
                sample.Background.B,
                createdUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }

        var manifest = new ProbeRingManifest(
            scale,
            createdUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            entries);
        File.WriteAllText(
            Path.Combine(folder, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>Reads a saved run back: manifest plus every PNG, weights re-derived from the pixels.</summary>
    /// <param name="folder">A folder written by <see cref="Save"/>.</param>
    internal static ProbeRingArchive Load(string folder)
    {
        var manifestPath = Path.Combine(folder, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<ProbeRingManifest>(
                File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidOperationException($"The capture manifest at {manifestPath} is empty or unreadable.");

        var samples = manifest.Samples.Select(entry => ReadSample(folder, entry)).ToList();
        return new ProbeRingArchive(
            samples,
            manifest.Scale,
            DateTimeOffset.Parse(manifest.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>The PNG file name for one sample: condition, fraction and repeat, invariant formatting.</summary>
    internal static string SampleFileName(ProbeRingCapturedSample sample) =>
        $"{sample.Kind}_{sample.Condition}_f{sample.Fraction.ToString("0.00", CultureInfo.InvariantCulture)}_r{sample.Repeat}.png";

    private static void WritePng(string path, ProbeRingCapturedSample sample)
    {
        var source = BitmapSource.Create(
            sample.SizePx, sample.SizePx, 96, 96, PixelFormats.Bgr32, null, sample.Bgra, sample.SizePx * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ProbeRingCapturedSample ReadSample(string folder, ProbeRingManifestSample entry)
    {
        var path = Path.Combine(folder, entry.File);
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource readable = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0.0);

        var stride = readable.PixelWidth * 4;
        var pixels = new byte[stride * readable.PixelHeight];
        readable.CopyPixels(pixels, stride, 0);

        var background = (entry.BackgroundR, entry.BackgroundG, entry.BackgroundB);
        var weights = ProbeRingCaptureAnalysis.ToWeights(pixels, readable.PixelWidth, readable.PixelHeight, background);
        var geometry = new ProbeRingFrameSchedule.Frame(
            entry.Fraction,
            new PhysicalPoint(entry.WindowOriginX, entry.WindowOriginY),
            new PhysicalPoint(
                entry.CaptureCentreX - entry.WindowOriginX,
                entry.CaptureCentreY - entry.WindowOriginY),
            new PhysicalPoint(entry.CaptureCentreX, entry.CaptureCentreY),
            entry.CaptureOriginX,
            entry.CaptureOriginY);

        return new ProbeRingCapturedSample(
            entry.Kind, entry.Condition, entry.Fraction, entry.Repeat, geometry, pixels,
            readable.PixelWidth, weights, background);
    }
}
