namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// H2 (fixup 02): the resume reader. It must rebuild a convicting session-1 verdict from the newest
/// matching result file, skip acquitting, session-2 and unreadable files, and fall back to "no
/// resume" without throwing.
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

    private static void WriteSession1(
        string path,
        SubPixelAbOutcome outcome,
        SubPixelAbStaticReadout readout = SubPixelAbStaticReadout.CannotTellStillRingsApart,
        string logLine = "Sub-pixel A/B — drift 9/10 → session 1 CONVICTS")
    {
        var writer = new SubPixelAbResultWriter(path, Info());
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Drift, aPicks: 1, bPicks: 9));
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Static, aPicks: 6, bPicks: 4));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session1, SubPixelProbeEndReason.BlocksCompleted, false, []),
            new SubPixelAbSession1Verdict(outcome, readout, logLine),
            null);
    }

    [Fact]
    public void AConvictingResult_RoundTripsIntoTheSameVerdict()
    {
        var directory = TempTestDirectory.Create("reader-roundtrip");
        var path = Path.Combine(directory, "subpixel-ab-20260101-000000000.json");
        const string logLine = "Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (static 6/10: cannot tell still rings apart)";
        WriteSession1(path, SubPixelAbOutcome.Convicts, SubPixelAbStaticReadout.CannotTellStillRingsApart, logLine);

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.CannotTellStillRingsApart);
        Check.That(verdict.LogLine).IsEqualTo(logLine);
        Check.That(SubPixelProbeViewModel.CanResumeSession2(verdict)).IsTrue();
    }

    [Fact]
    public void AnAcquittingNewerFile_IsSkipped_InFavourOfAnOlderConvictingOne()
    {
        var directory = TempTestDirectory.Create("reader-newest");
        var convicting = Path.Combine(directory, "subpixel-ab-old.json");
        var acquitting = Path.Combine(directory, "subpixel-ab-new.json");
        WriteSession1(convicting, SubPixelAbOutcome.Convicts);
        WriteSession1(acquitting, SubPixelAbOutcome.Acquits);
        File.SetLastWriteTimeUtc(convicting, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(acquitting, DateTime.UtcNow);

        var verdict = SubPixelAbResultReader.ReadNewestConvictingSession1(directory);

        Check.WithCustomMessage("The reader returned a file whose session 1 did not convict")
            .That(verdict).IsNotNull();
        Check.That(verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void ASession2File_IsNeverResumedFrom()
    {
        var directory = TempTestDirectory.Create("reader-session2");
        var path = Path.Combine(directory, "subpixel-ab-session2.json");
        var writer = new SubPixelAbResultWriter(path, Info(), SubPixelProbeStartMode.ResumeSession2);
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Live, aPicks: 2, bPicks: 8));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session2, SubPixelProbeEndReason.BlocksCompleted, false, []),
            null,
            new SubPixelAbSession2Verdict(SubPixelAbOutcome.Convicts, "live 8/10 → CONVICTS"));

        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void MissingDirectory_CorruptFile_AndAcquittalOnly_AllReadAsNoResume()
    {
        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(
                Path.Combine(Path.GetTempPath(), "eyectrl-no-such-dir-" + Guid.NewGuid().ToString("N"))))
            .IsNull();

        var directory = TempTestDirectory.Create("reader-mixed");
        File.WriteAllText(Path.Combine(directory, "subpixel-ab-corrupt.json"), "{ this is not json");
        Check.WithCustomMessage("A corrupt result must not throw and must read as no resume")
            .That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();

        WriteSession1(Path.Combine(directory, "subpixel-ab-acquit.json"), SubPixelAbOutcome.Acquits);
        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }

    [Fact]
    public void AnIncompleteSession1_ReadsAsNoResume()
    {
        var directory = TempTestDirectory.Create("reader-incomplete");
        var path = Path.Combine(directory, "subpixel-ab-incomplete.json");
        var writer = new SubPixelAbResultWriter(path, Info());
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session1, SubPixelProbeEndReason.EndTest, true, []),
            new SubPixelAbSession1Verdict(
                SubPixelAbOutcome.Incomplete, SubPixelAbStaticReadout.Skipped, "drift incomplete"),
            null);

        Check.That(SubPixelAbResultReader.ReadNewestConvictingSession1(directory)).IsNull();
    }
}
