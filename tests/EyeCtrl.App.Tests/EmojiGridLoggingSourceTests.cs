namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using NFluent;
using Xunit;

/// <summary>
/// Source-scan pins for the emoji grid's log contract — the headless half of what
/// <c>KeyboardCategoryGridExclusionTests.CategoryActivation_AndTheGridCollapse_LeaveLogEvidence</c>
/// proves at runtime.
///
/// <para>
/// The runtime test is the stronger instrument: it asserts the emitted payload, the cell count
/// measured against the registrar's own dictionary, and the absence of glyphs, none of which a
/// source scan can see. But it lives in the WPF collection and has to install a capturing sink to
/// observe anything. These pins need neither, and they catch the shapes that make the log lie or
/// vanish: a line demoted below <c>Information</c> (invisible in the shipped file), the activation
/// line moved inside the change-guard (vacuity by placement — an already-active tab re-firing is
/// just as much of a consumed dwell, but says nothing), a logging call appearing on the per-sample
/// progress callback, or on the one method that receives the user's selected glyph.
/// </para>
///
/// <para>
/// Comments are stripped before every scan, for the reason the census tests record: this file's own
/// documentation names the very calls it forbids, and a scan over raw text would read them.
/// </para>
/// </summary>
public class EmojiGridLoggingSourceTests
{
    private static string ReadSource() =>
        SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Overlays", "EmojiGridManager.cs")));

    /// <summary>
    /// The text of one method body, from its signature to the first closing brace back at method
    /// indentation. Deliberately a shape pin: both guards below fail loudly rather than returning a
    /// slice that would make the assertions pass over nothing.
    /// </summary>
    private static string Body(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage(
                $"EmojiGridManager no longer declares '{signature}', so this pin cannot read its " +
                "body — and a body read as empty would pass vacuously.")
            .That(start).IsStrictlyGreaterThan(-1);

        var end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.WithCustomMessage(
                $"The body of '{signature}' has no closing brace at method indentation, so the " +
                "scan below would read into the rest of the file.")
            .That(end).IsStrictlyGreaterThan(start);

        return text[start..end];
    }

    [Fact]
    public void EveryLogSite_StaysAtInformation_AndTheActivationLinePrecedesTheChangeGuard()
    {
        var text = ReadSource();

        Check.WithCustomMessage(
                "The emoji grid's log calls must stay at Information: LoggingInitializer runs at " +
                "MinimumLevel.Information, so a Debug or Verbose line reaches no file at all. The " +
                "surface's first log lines must not repeat KeyboardCoordinator's dead text-input " +
                "Debug line, which has never appeared in a log.")
            .That(text.Contains("Log.Debug(") || text.Contains("Log.Verbose(")).IsFalse();

        foreach (var site in new[]
                 {
                     "public void Enter()",
                     "public void Exit()",
                     "public void BuildGrid()",
                     "private void OnCategoryDwelled(string targetId)",
                     "private void ShowSkinToneSelector()",
                 })
        {
            Check.WithCustomMessage(
                    $"{site} no longer logs at Information. The mode boundaries, every rebuild " +
                    "(with the live cell count), every tab activation and the skin-tone selector " +
                    "are the events a two-minute session has to be readable from; losing one " +
                    "silently re-blinds the next live run, which is the whole ticket.")
                .That(Body(text, site).Contains("Log.Information(")).IsTrue();
        }

        var activation = Body(text, "private void OnCategoryDwelled(string targetId)");
        var changedAt = activation.IndexOf("var changed", StringComparison.Ordinal);
        var guardAt = activation.IndexOf("if (changed)", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The change-guard left OnCategoryDwelled, so this pin can no longer tell a line " +
                "before the guard from one inside it.")
            .That(guardAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The changed-flag computation left OnCategoryDwelled; repoint this pin rather " +
                "than deleting it (see the placement assertion below).")
            .That(changedAt).IsStrictlyGreaterThan(-1);

        var logAt = activation.IndexOf("Log.Information(", changedAt, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "No Information line is emitted after the activation's changed flag is computed, " +
                "so an activation with no category change logs nothing — and that re-fire is the " +
                "one a parked gaze produces.")
            .That(logAt).IsStrictlyGreaterThan(changedAt);
        Check.WithCustomMessage(
                "The category-activation line sits inside the change-guard (or after it). It must " +
                "report the DETECTOR firing, not the grid changing: an already-active tab " +
                "re-firing consumes the user's dwell and enters cooldown exactly like a switch, " +
                "so moving this line inside `if (changed)` hides the quieter half of the defect " +
                "while every runtime assertion on a first-time activation stays green.")
            .That(logAt).IsStrictlyLessThan(guardAt);
    }

    [Fact]
    public void PerSampleCallback_AndTheSelectedGlyphPath_DoNotLog()
    {
        var text = ReadSource();

        Check.WithCustomMessage(
                "OnCategoryProgress runs per gaze sample. Logging there is the dwell-progress " +
                "stream the logging design excludes — and the volume that buries the events this " +
                "ticket exists to make readable.")
            .That(Body(text, "private void OnCategoryProgress(string targetId, double progress)")
                .Contains("Log.")).IsFalse();

        Check.WithCustomMessage(
                "TrackEmojiInRecents receives the glyph the user selected. A logging call here is " +
                "how a keystroke log of their only communication channel gets written to disk in " +
                "plain text and rotated into files that outlive the session.")
            .That(Body(text, "public void TrackEmojiInRecents(string output)").Contains("Log.")).IsFalse();
    }
}
