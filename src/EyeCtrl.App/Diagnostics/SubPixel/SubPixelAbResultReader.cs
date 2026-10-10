namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// Reads a persisted session-1 verdict back for the "continue (live gaze)" entry (fixup 02 H2; test
/// design: "Later → exit; the same button resumes at session 2"). It rebuilds the verdict from the
/// newest <c>subpixel-ab-*.json</c> whose session 1 convicted and that holds no session 2, using the
/// writer's <b>same</b> <see cref="SubPixelAbResultWriter.JsonOptions"/> so reader and writer cannot
/// drift.
///
/// <para>
/// Every failure — a missing directory, no matching file, malformed JSON, a missing field — reads as
/// "no resume": the button falls back to a full run, never throws (fixup 02 H2).
/// </para>
/// </summary>
internal static class SubPixelAbResultReader
{
    /// <summary>
    /// The newest convicting session-1 verdict under <paramref name="resultsDirectory"/>, or
    /// <c>null</c> when there is none.
    /// </summary>
    /// <param name="resultsDirectory">The directory the results writer writes into.</param>
    internal static SubPixelAbSession1Verdict? ReadNewestConvictingSession1(string resultsDirectory)
    {
        try
        {
            if (!Directory.Exists(resultsDirectory)) return null;

            var newestFirst = Directory
                .EnumerateFiles(resultsDirectory, "subpixel-ab-*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal);

            foreach (var file in newestFirst)
            {
                if (TryRead(file.FullName) is { } verdict) return verdict;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read a sub-pixel probe result for the resume entry");
            return null;
        }
    }

    private static SubPixelAbSession1Verdict? TryRead(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<ReaderDocument>(
                File.ReadAllText(path), SubPixelAbResultWriter.JsonOptions);
            if (document?.Sessions is not { } sessions) return null;

            // A session-2 run already continued this conviction; never resume from it again.
            if (sessions.Any(session => session.Session == SubPixelProbeSessionKind.Session2)) return null;

            var session1 = sessions.FirstOrDefault(session => session.Session == SubPixelProbeSessionKind.Session1);
            if (session1 is null || session1.Outcome != SubPixelAbOutcome.Convicts) return null;

            return new SubPixelAbSession1Verdict(
                session1.Outcome.Value,
                session1.StaticReadout ?? SubPixelAbStaticReadout.Skipped,
                session1.LogLine ?? string.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ignoring an unreadable sub-pixel probe result {Path}", path);
            return null;
        }
    }

    /// <summary>The document fields the resume reader needs; the writer's own enums and names.</summary>
    internal sealed record ReaderDocument(List<ReaderSession> Sessions);

    /// <summary>One persisted session row; absent nullable fields stay null.</summary>
    internal sealed record ReaderSession(
        SubPixelProbeSessionKind Session,
        SubPixelAbOutcome? Outcome,
        SubPixelAbStaticReadout? StaticReadout,
        string? LogLine);
}
