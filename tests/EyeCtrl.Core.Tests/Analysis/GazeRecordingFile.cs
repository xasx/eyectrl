namespace EyeCtrl.Core.Tests.Analysis;

using System.Globalization;
using System.Text.Json;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// Reads back what <see cref="GazeRecorder"/> writes: a <c>#</c>-prefixed JSON header line followed by
/// CSV <c>x,y,timestampUs,isValid</c> sample lines.
/// <para>
/// Offline only — this lives in the test project, not on the shipping path, because nothing the
/// application does at runtime reads a recording. It exists so the analyzer consumes the real file
/// format rather than a convenient parallel one, which is the only way a recording made on the device
/// is guaranteed to be analyzable.
/// </para>
/// </summary>
public static class GazeRecordingFile
{
    /// <summary>A parsed recording: its session side-channel and the raw samples, in file order.</summary>
    /// <param name="Header">The session header, or a default when the file carried none.</param>
    /// <param name="Samples">Raw samples exactly as recorded — invalid ones included, since gaps are data.</param>
    /// <param name="MalformedLines">Lines that could not be parsed. Non-zero means a truncated or corrupt file.</param>
    /// <param name="Trailer">
    /// The closing trailer, or <c>null</c> when the file has none — which means the recorder never
    /// closed cleanly (the process died, or the writer faulted), so the file's completeness is unknown.
    /// </param>
    public sealed record Recording(
        GazeRecordingHeader Header,
        IReadOnlyList<GazePoint> Samples,
        int MalformedLines,
        GazeRecordingTrailer? Trailer)
    {
        /// <summary>
        /// Why this recording must not be measured, or <c>null</c> when it may be.
        /// <para>
        /// The three refusals are one idea: a metric computed over a file with holes in it is
        /// indistinguishable from a metric computed over a clean one, and the holes look exactly like
        /// the tracker dropouts these recordings exist to quantify. A dropped sample inflates the
        /// measured gap rate, a missing trailer means nobody counted, and malformed lines mean the file
        /// was cut. All three produce a number that is wrong in a way no reader can see.
        /// </para>
        /// </summary>
        public string? UnusableReason =>
            Trailer is null
                ? "the recording has no trailer, so it was never closed cleanly (crash, or a writer fault mid-session) and its completeness is unknown"
            : Trailer.Value.DroppedSamples > 0
                ? $"the recorder dropped {Trailer.Value.DroppedSamples} of {Trailer.Value.RecordedSamples} samples; the holes are indistinguishable from tracker dropouts"
            : MalformedLines > 0
                ? $"{MalformedLines} line(s) could not be parsed, so the file is truncated or corrupt"
            : Header.CalibrationActive && Header.CalibrationTransform is null
                ? "the session ran with a calibration whose transform the recorder could not serialize, so replay cannot reproduce the corrected stream the pipeline actually saw"
            : null;

        /// <summary>
        /// Throws unless this recording is complete enough to measure — a lossy recording must cost the
        /// operator a re-collection rather than produce a plausible number.
        /// <para>
        /// Called by every entry point that takes a <see cref="Recording"/>, which is every path a real
        /// file can travel. <c>ReplayPipeline.RunSynthetic</c> does NOT call it, and cannot: it takes a
        /// bare sample list, which is what the generated streams are, and there is no recording there to
        /// judge. That is the honest boundary — the protection is "you cannot analyze a FILE without
        /// passing the gate", not "no code path exists that skips it". The name is the guard: pointing
        /// <c>RunSynthetic</c> at a real recording's samples is visible at the call site.
        /// </para>
        /// </summary>
        public Recording EnsureUsable()
        {
            if (UnusableReason is { } why)
                throw new InvalidOperationException(
                    $"This gaze recording cannot be analyzed: {why}. Re-collect the session.");
            return this;
        }
    }

    /// <summary>Parses a recording from disk.</summary>
    /// <remarks>
    /// Read SHARED, not with <c>File.ReadAllLines</c>'s FileShare.Read: the recorder's writer opens its
    /// appends with FileShare.Read, and it can still be live here — <c>Dispose</c> joins it on a 2 s
    /// budget, so a starved writer outlives it. A stricter read collides with that handle and throws
    /// <see cref="System.IO.IOException"/> instead of reporting what is on disk. Callers that need the
    /// file to be complete wait for the writer's own close marker (the trailer) first.
    /// </remarks>
    public static Recording Read(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return Parse(lines);
    }

    /// <summary>Parses a recording from already-read lines.</summary>
    public static Recording Parse(IEnumerable<string> lines)
    {
        GazeRecordingHeader header = default;
        GazeRecordingTrailer? trailer = null;
        var samples = new List<GazePoint>();
        int malformed = 0;

        foreach (var raw in lines)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;

            if (line.StartsWith(GazeRecorder.TrailerPrefix))
            {
                // Checked before the '#' branch — the trailer marker starts with one. A second trailer
                // would mean two sessions appended into one file, so the LAST wins: its counts are the
                // ones covering the samples at the end of the file.
                try { trailer = JsonSerializer.Deserialize<GazeRecordingTrailer>(line[GazeRecorder.TrailerPrefix.Length..].ToString(), JsonOptions); }
                catch (JsonException) { malformed++; }
                continue;
            }

            if (line[0] == '#')
            {
                // A rotated file repeats the header, so later ones are ignored rather than treated as
                // an error: the first one describes the session either way.
                if (header == default)
                {
                    try { header = JsonSerializer.Deserialize<GazeRecordingHeader>(line[1..].ToString(), JsonOptions); }
                    catch (JsonException) { malformed++; }
                }
                continue;
            }

            if (TryParseSample(line, out var sample)) samples.Add(sample);
            else malformed++;
        }

        return new Recording(header, samples, malformed, trailer);
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static bool TryParseSample(ReadOnlySpan<char> line, out GazePoint sample)
    {
        sample = default;

        int c1 = line.IndexOf(',');
        if (c1 < 0) return false;
        int c2 = line[(c1 + 1)..].IndexOf(',');
        if (c2 < 0) return false;
        c2 += c1 + 1;
        int c3 = line[(c2 + 1)..].IndexOf(',');
        if (c3 < 0) return false;
        c3 += c2 + 1;

        if (!double.TryParse(line[..c1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return false;
        if (!double.TryParse(line[(c1 + 1)..c2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
        if (!long.TryParse(line[(c2 + 1)..c3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) return false;

        var validSpan = line[(c3 + 1)..].Trim();
        if (validSpan.Length != 1 || (validSpan[0] != '0' && validSpan[0] != '1')) return false;

        sample = new GazePoint(x, y, t, validSpan[0] == '1');
        return true;
    }
}
