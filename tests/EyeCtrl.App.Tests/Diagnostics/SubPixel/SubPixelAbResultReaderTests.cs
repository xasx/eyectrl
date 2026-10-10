namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// H2 + N1 + N7 (fixup 03): the resume reader. The newest <b>decisive</b> file ends the scan — a
/// file with at least one non-aborted session row. Aborted, half-started or empty runs say nothing
/// and are skipped; a corrupt file is skipped and logged, so it can never hide an older conviction.
/// </summary>
public class SubPixelAbResultReaderTests
{
    private static SubPixelAbRunInfo Info() => new(
        Seed: 7,
        Ring: new SubPixelAbRingInfo("WeightedSandwich", 250, 3, 0.7, "#00FF00"),
        AnswerDwellMs: 700,
        DpiScale: 2.0,
        ScreenBoundsPx: new PhysicalRect(0, 0, 2560, 1440));

    private static SubPixelProbeBlockOutcome BlockOutcome(SubPixelAbBlock block, int aPicks, int bPicks)
    {
        var trial = new SubPixelAbTrial(
            block, 0, SubPixelAbOrder.AFirst,
            new SubPixelAbStaticMotion(new PhysicalPoint(100.5, 200.5)));
        return new SubPixelProbeBlockOutcome(
            block,
            new SubPixelAbBlockTally(aPicks, bPicks, 0),
            [new SubPixelProbeTrialOutcome(trial, SubPixelAbCondition.B, null, 1000, 0, 0)]);
    }

    private const string ConvictingLogLine =
        "Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (static 6/10: cannot tell still rings apart)";

    /// <summary>Writes a session-1 result; aborted, acquitting and incomplete variants included.</summary>
    private static string WriteSession1(
        string directory,
        string fileName,
        SubPixelAbOutcome outcome,
        bool aborted = false,
        SubPixelAbStaticReadout readout = SubPixelAbStaticReadout.CannotTellStillRingsApart)
    {
        var path = Path.Combine(directory, fileName);
        var writer = new SubPixelAbResultWriter(path, Info());
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Drift, aPicks: 1, bPicks: 9));
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Static, aPicks: 6, bPicks: 4));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session1,
                aborted ? SubPixelProbeEndReason.EndTest : SubPixelProbeEndReason.BlocksCompleted,
                aborted,
                []),
            new SubPixelAbSession1Verdict(outcome, readout, ConvictingLogLine),
            null);
        return path;
    }

    /// <summary>Writes a resumed run's file: a session-2 row, completed or aborted.</summary>
    private static string WriteSession2(
        string directory, string fileName, SubPixelAbOutcome outcome, bool aborted = false)
    {
        var path = Path.Combine(directory, fileName);
        var writer = new SubPixelAbResultWriter(path, Info(), SubPixelProbeStartMode.ResumeSession2);
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Live, aPicks: 2, bPicks: 8));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session2,
                aborted ? SubPixelProbeEndReason.EndTest : SubPixelProbeEndReason.BlocksCompleted,
                aborted,
                []),
            null,
            new SubPixelAbSession2Verdict(outcome, "live 8/10 → CONVICTS"));
        return path;
    }

    private static void MakeOlder(string path, int minutes = 30) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutes));

    [Fact]
    public void AConvictingResult_RoundTripsIntoTheSameVerdict()
    {
        var directory = TempTestDirectory.Create("reader-roundtrip");
        WriteSession1(directory, "subpixel-ab-old.json", SubPixelAbOutcome.Convicts);

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.CannotTellStillRingsApart);
        Check.That(verdict.LogLine).IsEqualTo(ConvictingLogLine);
        Check.That(SubPixelProbeViewModel.CanResumeSession2(verdict)).IsTrue();
    }

    // ---- N1: the newest decisive file decides ----------------------------------------------------

    [Fact]
    public void ANewerCompletedResumedSession2_EndsTheResumeOffer()
    {
        var directory = TempTestDirectory.Create("reader-after-resume");
        MakeOlder(WriteSession1(directory, "subpixel-ab-conviction.json", SubPixelAbOutcome.Convicts));
        WriteSession2(directory, "subpixel-ab-resumed.json", SubPixelAbOutcome.Convicts);

        Check.WithCustomMessage(
                "After a completed session 2 the button must stop offering a resume; the old conviction "
                + "must not resurrect it")
            .That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void ANewerAcquittingFullRun_SupersedesAnOlderConviction()
    {
        var directory = TempTestDirectory.Create("reader-newer-acquit");
        MakeOlder(WriteSession1(directory, "subpixel-ab-conviction.json", SubPixelAbOutcome.Convicts));
        WriteSession1(directory, "subpixel-ab-new.json", SubPixelAbOutcome.Acquits);

        Check.WithCustomMessage("A newer full run that acquitted must supersede the older conviction")
            .That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void ANewerResumedRunAbortedInSession2_StillOffersTheOlderConviction()
    {
        var directory = TempTestDirectory.Create("reader-resume-aborted");
        var conviction = WriteSession1(directory, "subpixel-ab-conviction.json", SubPixelAbOutcome.Convicts);
        MakeOlder(conviction);
        WriteSession2(directory, "subpixel-ab-resumed.json", SubPixelAbOutcome.Acquits, aborted: true);

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.WithCustomMessage("An aborted resumed run says nothing; the owner may retry session 2")
            .That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void ANewerFullRunAbortedBeforeSession1Ended_StillOffersTheOlderConviction()
    {
        var directory = TempTestDirectory.Create("reader-aborted-early");
        MakeOlder(WriteSession1(directory, "subpixel-ab-conviction.json", SubPixelAbOutcome.Convicts));
        WriteSession1(directory, "subpixel-ab-aborted.json", SubPixelAbOutcome.Acquits, aborted: true);

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void ASession2File_IsNeverResumedFrom()
    {
        var directory = TempTestDirectory.Create("reader-session2");
        WriteSession2(directory, "subpixel-ab-session2.json", SubPixelAbOutcome.Convicts);

        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void AFileHoldingBothSessions_NeverOffersAResume()
    {
        // The rule is "the first decisive file: a session 2 → no resume", which must hold even for a
        // file that also carries a convicting session 1 — the shape a future writer version could
        // produce. Without the session-2 branch the session-1 verdict would win and loop.
        var directory = TempTestDirectory.Create("reader-both-sessions");
        var path = Path.Combine(directory, "subpixel-ab-both.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sessions = new object[]
            {
                new
                {
                    session = "session1", outcome = "convicts",
                    staticReadout = "cannotTellStillRingsApart", logLine = "line", aborted = false,
                },
                new { session = "session2", outcome = "convicts", aborted = false },
            },
        }, SubPixelAbResultWriter.JsonOptions));

        Check.WithCustomMessage("A file holding a session 2 must never offer a resume")
            .That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void AnIncompleteSession1_ReadsAsNoResume()
    {
        var directory = TempTestDirectory.Create("reader-incomplete");
        WriteSession1(directory, "subpixel-ab-incomplete.json", SubPixelAbOutcome.Incomplete,
            readout: SubPixelAbStaticReadout.Skipped);

        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    // ---- N7: corrupt files are skipped, never fatal ----------------------------------------------

    [Fact]
    public void ACorruptNewerFile_DoesNotHideAnOlderConviction()
    {
        var directory = TempTestDirectory.Create("reader-corrupt-newer");
        MakeOlder(WriteSession1(directory, "subpixel-ab-conviction.json", SubPixelAbOutcome.Convicts));
        File.WriteAllText(Path.Combine(directory, "subpixel-ab-corrupt.json"), "{ this is not json");

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.WithCustomMessage("A corrupt newest file aborted the scan and hid the older conviction")
            .That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void MissingDirectory_AndCorruptionOnly_ReadAsNoResume()
    {
        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(
                Path.Combine(Path.GetTempPath(), "eyectrl-no-such-dir-" + Guid.NewGuid().ToString("N"))))
            .IsNull();

        var directory = TempTestDirectory.Create("reader-corrupt-only");
        File.WriteAllText(Path.Combine(directory, "subpixel-ab-corrupt.json"), "{ this is not json");
        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }
}
