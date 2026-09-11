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
    public sealed record Recording(
        GazeRecordingHeader Header,
        IReadOnlyList<GazePoint> Samples,
        int MalformedLines);

    /// <summary>Parses a recording from disk.</summary>
    public static Recording Read(string path) => Parse(File.ReadAllLines(path));

    /// <summary>Parses a recording from already-read lines.</summary>
    public static Recording Parse(IEnumerable<string> lines)
    {
        GazeRecordingHeader header = default;
        var samples = new List<GazePoint>();
        int malformed = 0;

        foreach (var raw in lines)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;

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

        return new Recording(header, samples, malformed);
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
