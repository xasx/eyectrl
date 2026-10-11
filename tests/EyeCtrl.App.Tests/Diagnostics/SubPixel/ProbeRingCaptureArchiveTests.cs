namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Fixup 03 item 5's archive, headless: the run folder lives under %TEMP% (never
/// %APPDATA%\EyeCtrl, which SP-6 reads), every capture is a lossless PNG, the manifest records what
/// the offline analysis needs, and the whole thing round-trips bit-identically.
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingCaptureArchiveTests
{
    /// <summary>
    /// Fixup 06 R2d: the manifest's sha rule, pure and unit-tested. A dropped dirty flag (S1), a
    /// failed status probe recording a clean-looking sha, and an empty sha are each caught here.
    /// </summary>
    [Theory]
    [InlineData("abc123", "", true, "abc123")]
    [InlineData("abc123", " M file.cs\n", true, "abc123-dirty")]
    [InlineData("abc123", "", false, "abc123-unverified")]
    [InlineData(null, "", true, "unknown")]
    [InlineData("", " M x", true, "unknown")]
    public void FormatCommitSha_IsTheManifestRule(string? sha, string porcelain, bool statusOk, string expected)
    {
        Check.WithCustomMessage(
                $"FormatCommitSha({sha ?? "null"}, \"{porcelain.Replace("\n", "\\n")}\", {statusOk}) must be {expected}.")
            .That(ProbeRingCaptureArchive.FormatCommitSha(sha, porcelain, statusOk)).IsEqualTo(expected);
    }

    /// <summary>
    /// Fixup 07 item 1: git prints forward slashes on Windows while a DirectoryInfo uses
    /// backslashes; the toplevel comparison must normalise, or it is always false and the sha never
    /// leaves "unknown".
    /// </summary>
    [Fact]
    public void PathsEqual_NormalisesSeparators()
    {
        Check.WithCustomMessage("git's forward-slash form and the DirectoryInfo form are the same path.")
            .That(ProbeRingCaptureArchive.PathsEqual("C:/t/x", @"C:\t\x")).IsTrue();
        Check.WithCustomMessage("A trailing separator must not matter.")
            .That(ProbeRingCaptureArchive.PathsEqual(@"C:\t\x\", "C:/t/x")).IsTrue();
        Check.WithCustomMessage("Different paths must differ.")
            .That(ProbeRingCaptureArchive.PathsEqual("C:/t/x", @"C:\t\y")).IsFalse();
    }

    /// <summary>
    /// Fixup 07 item 1: wherever git resolves the toplevel at the repo root, the recorded sha must
    /// be a real 40-hex sha (optionally -dirty/-unverified) — never "unknown". In a tree without
    /// git (a git archive extract), this asserts nothing.
    /// </summary>
    [Fact]
    public void CurrentCommitSha_IsARealSha_WhereGitResolvesTheRepoRoot()
    {
        var root = SourceScanner.RepoRoot().FullName;

        string? topLevel = null;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
            });
            if (git is not null)
            {
                topLevel = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit(5000);
                if (git.ExitCode != 0)
                    topLevel = null;
            }
        }
        catch
        {
            topLevel = null;
        }

        if (topLevel is null || !ProbeRingCaptureArchive.PathsEqual(topLevel, root))
            return;   // no git here, or a different repository: "unknown" is the correct record

        Check.WithCustomMessage(
                $"Git resolves the repo root here, so the manifest sha must be real; got "
                + $"'{ProbeRingCaptureArchive.CurrentCommitSha}'.")
            .That(ProbeRingCaptureArchive.CurrentCommitSha).IsNotEqualTo("unknown");
        Check.WithCustomMessage(
                $"The sha must be 40 hex, optionally -dirty/-unverified; got "
                + $"'{ProbeRingCaptureArchive.CurrentCommitSha}'.")
            .That(Regex.IsMatch(ProbeRingCaptureArchive.CurrentCommitSha, @"^[0-9a-f]{40}(-dirty|-unverified)?$"))
            .IsTrue();
    }

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
        var samples = ProbeRingSyntheticRun.Small();
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

                Check.That(after.Kind).IsEqualTo(before.Kind);
                Check.That(after.Condition).IsEqualTo(before.Condition);
                Check.That(after.Fraction).IsEqualTo(before.Fraction);
                Check.That(after.Repeat).IsEqualTo(before.Repeat);
                Check.That(after.Background).IsEqualTo(before.Background);
                Check.WithCustomMessage("The look must round-trip (fixup 04 F5).")
                    .That(after.Look).IsEqualTo(before.Look);
                Check.That(after.ElapsedMs).IsEqualTo(before.ElapsedMs);
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
        var samples = ProbeRingSyntheticRun.Small();
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

            // Fixup 04 F5: the look, the per-capture time and the commit sha.
            Check.WithCustomMessage(
                    "The manifest must record the look each frame was drawn with, or a later code change "
                    + "silently changes the expected pattern a saved run is measured against.")
                .That(entry.Look)
                .IsEqualTo(ProbeRingCaptureScene.LookOf(ProbeRingCaptureScene.OwnerLook()));
            Check.WithCustomMessage("The manifest must record a per-capture timestamp (small by a Stopwatch).")
                .That(archive.Samples.Select(sample => sample.ElapsedMs).Distinct().Count())
                .IsStrictlyGreaterThan(1);
            Check.WithCustomMessage("The manifest must record the commit sha (or 'unknown').")
                .That(archive.CommitSha).IsNotNull();
            Check.WithCustomMessage(
                    $"The sha must be a real sha, optionally -dirty/-unverified, or 'unknown'; got '{archive.CommitSha}'.")
                .That(Regex.IsMatch(archive.CommitSha, @"^([0-9a-f]{40}(-dirty|-unverified)?|unknown)$")).IsTrue();
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
