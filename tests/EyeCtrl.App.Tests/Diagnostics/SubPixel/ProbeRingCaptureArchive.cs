namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The ring look a capture was drawn with (fixup 04 F5): rebuilt into <see cref="CursorSettings"/>
/// by the offline analyser, so a later code change cannot silently change the expected pattern a
/// saved run is measured against.
/// </summary>
/// <param name="Style">The <see cref="CursorRingStyle"/> name.</param>
/// <param name="SizeDip">The ring diameter in DIPs.</param>
/// <param name="Thickness">The core band's thickness in DIPs.</param>
/// <param name="Opacity">The user opacity.</param>
/// <param name="Colour">The user's ring colour, in hex.</param>
internal sealed record ProbeRingLook(string Style, double SizeDip, double Thickness, double Opacity, string Colour)
{
    /// <summary>Rebuilds the settings this look was drawn from.</summary>
    internal CursorSettings ToSettings() => new()
    {
        RingStyle = Enum.Parse<CursorRingStyle>(Style),
        CircleSize = SizeDip,
        CircleThickness = Thickness,
        CircleOpacity = Opacity,
        CircleColor = Colour,
    };
}

/// <summary>
/// One captured frame with everything the archive and the measurement need: what was scheduled,
/// the raw BGRA pixels (for the PNG), the estimator's weight raster, and the measured background.
/// <see cref="Kind"/> separates the reference repeats, the A/B assertion rows, the sweep rows and
/// the diagnostics: the assertion fractions 0.25/0.5/0.75 are also sweep fractions, so the
/// fraction alone cannot tell the rows apart.
/// </summary>
/// <param name="Kind">The row kind: see <see cref="ProbeRingOnScreenJudge.KindReference"/> and friends.</param>
/// <param name="Condition">"A" or "B".</param>
/// <param name="Fraction">The scheduled sub-pixel fraction (x; see the frame for y).</param>
/// <param name="Repeat">The capture's index within its row (0-based).</param>
/// <param name="Frame">The scheduled geometry the capture used.</param>
/// <param name="Bgra">The raw capture pixels, BGRA32 row-major.</param>
/// <param name="SizePx">The frame's side in pixels.</param>
/// <param name="Weights">The estimator raster derived from <paramref name="Bgra"/>.</param>
/// <param name="Background">The measured background colour.</param>
/// <param name="Look">The ring look this frame was drawn with.</param>
/// <param name="ElapsedMs">Milliseconds from the run's start to this capture (fixup 04 F5).</param>
internal sealed record ProbeRingCapturedSample(
    string Kind,
    string Condition,
    double Fraction,
    int Repeat,
    ProbeRingFrameSchedule.Frame Frame,
    byte[] Bgra,
    int SizePx,
    ProbeRaster Weights,
    (byte R, byte G, byte B) Background,
    ProbeRingLook Look,
    long ElapsedMs);

/// <summary>The saved run's manifest header (fixup 03 item 5; sha added by fixup 04 F5).</summary>
internal sealed record ProbeRingManifest(
    double Scale, string CommitSha, string CreatedUtc, IReadOnlyList<ProbeRingManifestSample> Samples);

/// <summary>One manifest entry: enough to rebuild a capture's frame, look, background and weight raster.</summary>
internal sealed record ProbeRingManifestSample(
    string File,
    string Kind,
    string Condition,
    double Fraction,
    int Repeat,
    long ElapsedMs,
    string LookStyle,
    double LookSizeDip,
    double LookThickness,
    double LookOpacity,
    string LookColour,
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

/// <summary>A loaded run: its samples, the scale they were captured at, when, and the commit.</summary>
internal sealed record ProbeRingArchive(
    IReadOnlyList<ProbeRingCapturedSample> Samples, double Scale, DateTimeOffset CreatedUtc, string CommitSha);

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
    /// The commit that produced a run, recorded once per manifest (fixup 04 F5): one run, one sha,
    /// so a per-capture copy would only duplicate a constant. "unknown" when git cannot be read.
    /// </summary>
    private static readonly Lazy<string> CommitSha = new(ReadCommitSha);

    /// <summary>The commit the current checkout is on, or "unknown"; cached for the process.</summary>
    internal static string CurrentCommitSha => CommitSha.Value;

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
                sample.ElapsedMs,
                sample.Look.Style,
                sample.Look.SizeDip,
                sample.Look.Thickness,
                sample.Look.Opacity,
                sample.Look.Colour,
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
            CurrentCommitSha,
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
            DateTimeOffset.Parse(manifest.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            manifest.CommitSha);
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
        var look = new ProbeRingLook(
            entry.LookStyle, entry.LookSizeDip, entry.LookThickness, entry.LookOpacity, entry.LookColour);

        return new ProbeRingCapturedSample(
            entry.Kind, entry.Condition, entry.Fraction, entry.Repeat, geometry, pixels,
            readable.PixelWidth, weights, background, look, entry.ElapsedMs);
    }

    /// <summary>
    /// The sha a manifest should record (fixup 06 R2d), pure so it can be unit-tested:
    /// <list type="bullet">
    /// <item>no sha → <c>unknown</c>;</item>
    /// <item>the status probe failed → <c>&lt;sha&gt;-unverified</c> (never a clean-looking sha);</item>
    /// <item>the working tree has changes → <c>&lt;sha&gt;-dirty</c>;</item>
    /// <item>otherwise the sha.</item>
    /// </list>
    /// </summary>
    /// <param name="sha">`git rev-parse HEAD` output, or null/empty.</param>
    /// <param name="porcelain">`git status --porcelain` output.</param>
    /// <param name="statusOk">Whether the status probe succeeded.</param>
    internal static string FormatCommitSha(string? sha, string? porcelain, bool statusOk)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "unknown";
        if (!statusOk)
            return sha.Trim() + "-unverified";
        return string.IsNullOrWhiteSpace(porcelain) ? sha.Trim() : sha.Trim() + "-dirty";
    }

    /// <summary>
    /// `git rev-parse HEAD` in the repo root, with the dirty/unverified suffix from
    /// <see cref="FormatCommitSha"/>, or "unknown" when git cannot be read or the repo root is not
    /// this checkout's top level (fixup 06 R2d: an archive extracted inside another repository must
    /// not record that repository's HEAD).
    /// </summary>
    private static string ReadCommitSha()
    {
        try
        {
            var root = EyeCtrl.App.Tests.SourceScanner.RepoRoot().FullName;

            var (topLevelOk, topLevel) = RunGit(root, "rev-parse --show-toplevel");
            if (!topLevelOk || !PathsEqual(topLevel.Trim(), root))
                return "unknown";

            var (shaOk, sha) = RunGit(root, "rev-parse HEAD");
            if (!shaOk)
                return FormatCommitSha(null, null, statusOk: false);

            var (statusOk, porcelain) = RunGit(root, "status --porcelain");
            return FormatCommitSha(sha, porcelain, statusOk);
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static (bool Ok, string Output) RunGit(string workingDirectory, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        });
        if (git is null)
            return (false, string.Empty);

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit(5000);
        return (git.ExitCode == 0, output);
    }
}
