namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// C11 — the results writer. The file is the test's only product, so these tests read it back:
/// per-block atomic writes, the session verdict before Ask, a run that never started a session, and
/// the resume header.
/// </summary>
public class SubPixelAbResultWriterTests
{
    private static SubPixelAbRunInfo Info() => new(
        Seed: 4242,
        Ring: new SubPixelAbRingInfo("WeightedSandwich", 250, 3, 0.7, "#00FF00"),
        AnswerDwellMs: 700,
        DpiScale: 2.0,
        ScreenBoundsPx: new PhysicalRect(0, 0, 2560, 1440));

    private static SubPixelProbeBlockOutcome BlockOutcome(SubPixelAbBlock block, int aPicks = 2, int bPicks = 8, int voids = 1)
    {
        var trial = new SubPixelAbTrial(
            block, 0, SubPixelAbOrder.AFirst,
            new SubPixelAbDriftMotion(new PhysicalPoint(100.25, 200.5), SubPixelAbDirection.SouthEast));
        return new SubPixelProbeBlockOutcome(
            block,
            new SubPixelAbBlockTally(aPicks, bPicks, voids),
            [
                new SubPixelProbeTrialOutcome(trial, SubPixelAbCondition.B, null, 1234, 0, 12),
                new SubPixelProbeTrialOutcome(trial, null, SubPixelProbeVoidReason.Silence, 0, 0, 51),
            ]);
    }

    private static JsonDocument Read(SubPixelAbResultWriter writer) =>
        JsonDocument.Parse(File.ReadAllText(writer.FilePath));

    [Fact]
    public void WriteBlock_WritesAtomically_WithTheRunAndTrialFacts()
    {
        var path = Path.Combine(TempTestDirectory.Create("writer-block"), "result.json");
        var writer = new SubPixelAbResultWriter(path, Info());

        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Drift));

        Check.That(File.Exists(path)).IsTrue();
        Check.WithCustomMessage("A temporary file was left behind (the write is not atomic)")
            .That(File.Exists(path + ".tmp")).IsFalse();

        using var doc = Read(writer);
        var root = doc.RootElement;
        Check.That(root.GetProperty("seed").GetInt32()).IsEqualTo(4242);
        Check.That(root.GetProperty("dpiScale").GetDouble()).IsEqualTo(2.0);
        Check.That(root.GetProperty("answerDwellMs").GetInt32()).IsEqualTo(700);
        Check.That(root.GetProperty("ring").GetProperty("style").GetString()).IsEqualTo("WeightedSandwich");

        var block = root.GetProperty("blocks")[0];
        Check.That(block.GetProperty("block").GetString()).IsEqualTo("drift");
        Check.That(block.GetProperty("aPicks").GetInt32()).IsEqualTo(2);
        Check.That(block.GetProperty("bPicks").GetInt32()).IsEqualTo(8);
        Check.That(block.GetProperty("voids").GetInt32()).IsEqualTo(1);
        Check.That(block.GetProperty("scored").GetInt32()).IsEqualTo(10);

        var scored = block.GetProperty("trials")[0];
        Check.That(scored.GetProperty("answer").GetString()).IsEqualTo("b");
        Check.That(scored.GetProperty("motionKind").GetString()).IsEqualTo("drift");
        Check.That(scored.GetProperty("startX").GetDouble()).IsEqualTo(100.25);
        Check.That(scored.GetProperty("responseTimeMs").GetInt64()).IsEqualTo(1234);

        var voided = block.GetProperty("trials")[1];
        Check.That(voided.GetProperty("voidReason").GetString()).IsEqualTo("silence");
        Check.That(voided.GetProperty("longestRenderGapMs").GetInt64()).IsEqualTo(51);
    }

    [Fact]
    public void EveryBlockAppends_SoAnAbortNeverLosesACompletedResult()
    {
        var path = Path.Combine(TempTestDirectory.Create("writer-append"), "result.json");
        var writer = new SubPixelAbResultWriter(path, Info());

        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Drift));
        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Static, aPicks: 6, bPicks: 4, voids: 0));

        using var doc = Read(writer);
        Check.That(doc.RootElement.GetProperty("blocks").GetArrayLength()).IsEqualTo(2);
        Check.That(doc.RootElement.GetProperty("blocks")[1].GetProperty("block").GetString())
            .IsEqualTo("static");
    }

    [Fact]
    public void WriteSession_CarriesTheVerdictAndTheStaticReadout()
    {
        var path = Path.Combine(TempTestDirectory.Create("writer-session"), "result.json");
        var writer = new SubPixelAbResultWriter(path, Info());
        var verdict = new SubPixelAbSession1Verdict(
            SubPixelAbOutcome.Convicts, SubPixelAbStaticReadout.CannotTellStillRingsApart,
            "Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (static 6/10: cannot tell still rings apart)");

        writer.WriteBlock(BlockOutcome(SubPixelAbBlock.Drift));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session1, SubPixelProbeEndReason.BlocksCompleted, false, []),
            verdict, null);

        using var doc = Read(writer);
        var session = doc.RootElement.GetProperty("sessions")[0];
        Check.That(session.GetProperty("session").GetString()).IsEqualTo("session1");
        Check.That(session.GetProperty("reason").GetString()).IsEqualTo("blocksCompleted");
        Check.That(session.GetProperty("outcome").GetString()).IsEqualTo("convicts");
        Check.That(session.GetProperty("staticReadout").GetString()).IsEqualTo("cannotTellStillRingsApart");
        Check.That(session.GetProperty("logLine").GetString()).Contains("CONVICTS");
    }

    [Fact]
    public void ARunThatNeverStartedASession_StillWritesItsRunFacts()
    {
        var path = Path.Combine(TempTestDirectory.Create("writer-empty"), "result.json");
        var writer = new SubPixelAbResultWriter(path, Info());

        writer.WriteRunEnd(SubPixelProbeRunEndReason.NoAnswer);

        using var doc = Read(writer);
        Check.That(doc.RootElement.GetProperty("blocks").GetArrayLength()).IsEqualTo(0);
        Check.That(doc.RootElement.GetProperty("sessions").GetArrayLength()).IsEqualTo(0);
        Check.That(doc.RootElement.GetProperty("runEndReason").GetString()).IsEqualTo("noAnswer");
        Check.That(doc.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
    }

    [Fact]
    public void AResume_RecordsItsStartModeAndTheSession1ItContinuesFrom()
    {
        var path = Path.Combine(TempTestDirectory.Create("writer-resume"), "result.json");
        var writer = new SubPixelAbResultWriter(
            path, Info(), SubPixelProbeStartMode.ResumeSession2, "drift 9/10 → CONVICTS");

        writer.WriteRunEnd(SubPixelProbeRunEndReason.Completed);

        using var doc = Read(writer);
        Check.That(doc.RootElement.GetProperty("startMode").GetString()).IsEqualTo("resumeSession2");
        Check.That(doc.RootElement.GetProperty("resumedSession1LogLine").GetString())
            .IsEqualTo("drift 9/10 → CONVICTS");
    }
}
