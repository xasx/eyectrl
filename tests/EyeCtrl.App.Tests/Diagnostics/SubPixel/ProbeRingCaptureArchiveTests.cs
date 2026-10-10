namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// Fixup 03 item 5's archive, headless: the run folder lives under %TEMP% (never
/// %APPDATA%\EyeCtrl, which SP-6 reads), every capture is a lossless PNG, the manifest records what
/// the offline analysis needs, and the whole thing round-trips bit-identically.
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingCaptureArchiveTests
{
    [Fact]
    public void CreateRunFolder_LivesUnderTemp_AndNeverUnderAppData()
    {
        var folder = ProbeRingCaptureArchive.CreateRunFolder(DateTimeOffset.UtcNow);

        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Check.WithCustomMessage($"The run folder must live under %TEMP%; got {folder}.")
            .That(folder.StartsWith(temp, StringComparison.OrdinalIgnoreCase)).IsTrue();

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        Check.WithCustomMessage(
                $"The run folder must never be written under %APPDATA%\\EyeCtrl — SP-6 reads that; got {folder}.")
            .That(folder.StartsWith(appData, StringComparison.OrdinalIgnoreCase)).IsFalse();

        Check.WithCustomMessage("The folder name must carry the eyectrl-r2- prefix and a stamp.")
            .That(Path.GetFileName(folder).StartsWith(ProbeRingCaptureArchive.FolderPrefix, StringComparison.Ordinal)).IsTrue();
    }

    [StaFact]
    public void Captures_RoundTripThroughTheArchive()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var loaded = ProbeRingCaptureArchive.Load(folder);

            Check.That(loaded.Scale).IsEqualTo(2.0);
            Check.That(loaded.Samples).HasSize(samples.Count);

            for (var i = 0; i < samples.Count; i++)
            {
                var before = samples[i];
                var after = loaded.Samples[i];

                Check.That(after.Condition).IsEqualTo(before.Condition);
                Check.That(after.Fraction).IsEqualTo(before.Fraction);
                Check.That(after.Repeat).IsEqualTo(before.Repeat);
                Check.That(after.Background).IsEqualTo(before.Background);
                Check.That(after.Frame.CaptureOriginX).IsEqualTo(before.Frame.CaptureOriginX);
                Check.That(after.Frame.CaptureOriginY).IsEqualTo(before.Frame.CaptureOriginY);
                Check.That(after.Frame.CaptureCentrePx.X).IsEqualTo(before.Frame.CaptureCentrePx.X);
                // The local centre is reconstructed as centre − origin, so its last bits can differ
                // from the direct sum; the measurement's geometry uses the same subtraction in both
                // paths, so the tolerance here is about the JSON round-trip, not the analysis.
                Check.That(after.Frame.RingLocalCentrePx.X)
                    .IsCloseTo(before.Frame.RingLocalCentrePx.X, 1e-9);

                Check.WithCustomMessage(
                        $"Sample {i}'s weight raster must round-trip bit-identically through the lossless "
                        + "PNG, or the offline analysis is measuring a different image.")
                    .That(WeightBytes(after.Weights).SequenceEqual(WeightBytes(before.Weights))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [StaFact]
    public void Manifest_RecordsWhatTheOfflineAnalysisNeeds()
    {
        var samples = ProbeRingSyntheticRun.Healthy(referenceRepeats: 1, rowRepeats: 1);
        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            var createdUtc = DateTimeOffset.UtcNow;
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, createdUtc);

            var manifestPath = Path.Combine(folder, ProbeRingCaptureArchive.ManifestFileName);
            Check.WithCustomMessage($"The manifest must exist at {manifestPath}.")
                .That(File.Exists(manifestPath)).IsTrue();

            var archive = ProbeRingCaptureArchive.Load(folder);
            var entry = archive.Samples.First(sample => sample.Condition == "B" && Math.Abs(sample.Fraction - 0.25) < 1e-9);

            Check.WithCustomMessage("The manifest must record the capture origin (the offline crop frame).")
                .That(entry.Frame.CaptureOriginX).IsEqualTo(1620);
            Check.That(entry.Frame.CaptureOriginY).IsEqualTo(900);
            Check.WithCustomMessage("The manifest must record the effective scale.")
                .That(archive.Scale).IsEqualTo(2.0);
            Check.WithCustomMessage("The manifest must record the run's UTC timestamp.")
                .That(Math.Abs((archive.CreatedUtc - createdUtc).TotalSeconds)).IsLessOrEqualThan(5.0);
            Check.WithCustomMessage("Every sample must name its PNG file on disk.")
                .That(archive.Samples.All(sample =>
                    File.Exists(Path.Combine(folder, ProbeRingCaptureArchive.SampleFileName(sample))))).IsTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] WeightBytes(ProbeRaster raster)
    {
        var weights = new byte[raster.Pixels.Length / 4];
        for (var i = 0; i < weights.Length; i++)
            weights[i] = raster.Pixels[i * 4 + 3];
        return weights;
    }
}
