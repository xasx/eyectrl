namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// Reads a persisted session-1 verdict back for the "continue (live gaze)" entry (fixup 02 H2; test
/// design: "Later → exit; the same button resumes at session 2"). It rebuilds the verdict from the
/// newest <b>decisive</b> <c>subpixel-ab-*.json</c>, using the writer's <b>same</b>
/// <see cref="SubPixelAbResultWriter.JsonOptions"/> so reader and writer cannot drift.
///
/// <para>
/// <b>The newest decisive file decides (fixup 03 N1).</b> Files are walked newest-first. A file is
/// decisive when it holds at least one non-aborted session row — a crashed, half-started or aborted
/// run says nothing about what came after, so it is skipped. The first decisive file ends the scan:
/// a non-aborted session 2 means the resume already happened (no further resume); a session 1 that
/// convicts means the button offers session 2 again; acquittal, incomplete or anything else means no
/// resume. This stops the button looping after a completed session 2, and lets a newer acquittal
/// supersede an older conviction.
/// </para>
///
/// <para>
/// Every failure — a missing directory, an unreadable file, a missing field — skips that file (and
/// logs it) without ending the scan, so a corrupt newest file never hides an older conviction.
/// </para>
/// </summary>
internal static class SubPixelAbResultReader
{
    /// <summary>
    /// The verdict the newest decisive result offers, or <c>null</c> when the button must start a
    /// full run.
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
                if (!TryReadDocument(file.FullName, out var document) || document?.Sessions is not { } sessions)
                    continue;

                // A file with no decisive session row says nothing; keep scanning older files. A
                // row is decisive only with an explicit session, aborted:false and outcome — a
                // tampered or future-schema row must never decide (fixup 04 HR10).
                var decisive = sessions.Where(session => session.IsDecisive).ToList();
                if (decisive.Count == 0) continue;

                // The first decisive file decides, whether or not it offers a resume.
                if (decisive.Any(session => session.Session == SubPixelProbeSessionKind.Session2))
                    return null;

                var session1 = decisive.FirstOrDefault(session =>
                    session.Session == SubPixelProbeSessionKind.Session1);
                if (session1?.Outcome != SubPixelAbOutcome.Convicts) return null;

                return new SubPixelAbSession1Verdict(
                    session1.Outcome.Value,
                    session1.StaticReadout ?? SubPixelAbStaticReadout.Skipped,
                    session1.LogLine ?? string.Empty);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read a sub-pixel probe result for the resume entry");
            return null;
        }
    }

    private static bool TryReadDocument(string path, out ReaderDocument? document)
    {
        document = null;
        try
        {
            document = JsonSerializer.Deserialize<ReaderDocument>(
                File.ReadAllText(path), SubPixelAbResultWriter.JsonOptions);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ignoring an unreadable sub-pixel probe result {Path}", path);
            return false;
        }
    }

    /// <summary>The document fields the resume reader needs; the writer's own enums and names.</summary>
    internal sealed record ReaderDocument(List<ReaderSession> Sessions);

    /// <summary>
    /// One persisted session row. Every field the decision needs is nullable so a row that omits
    /// one cannot masquerade as a completed session (fixup 04 HR10).
    /// </summary>
    internal sealed record ReaderSession(
        SubPixelProbeSessionKind? Session,
        SubPixelAbOutcome? Outcome,
        SubPixelAbStaticReadout? StaticReadout,
        string? LogLine,
        bool? Aborted)
    {
        /// <summary>Whether this row is complete enough (and not an abort) to decide a scan.</summary>
        internal bool IsDecisive => Session is not null && Aborted == false && Outcome is not null;
    }
}
