namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.IO;
using System.Linq;
using EyeCtrl.Core.Models;

/// <summary>
/// Fixup 03 item 6: the offline analyser, exercised headlessly on a synthetic folder. Whatever the
/// owner's next run shows, it can be re-measured from the saved PNGs without another on-screen run.
/// Fixup 04 F4 adds the live-vs-offline equality check that pins it.
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingOfflineAnalysisTests
{
    private readonly ITestOutputHelper _output;

    public ProbeRingOfflineAnalysisTests(ITestOutputHelper output) => _output = output;

    [StaFact]
    public void Analyse_OfASyntheticFolder_ReproducesTheLiveMeasurement()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);

            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            Check.WithCustomMessage(
                    "A healthy synthetic archive must re-measure cleanly; failures: "
                    + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
                .That(report.Failures).IsEmpty();

            var row = report.AssertionRows.Single(candidate =>
                candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.25) < 1e-9);
            Check.That(row.Repeats).IsEqualTo(ProbeRingOnScreenHarness.RowRepeats);
            Check.WithCustomMessage($"The offline B f=0.25 x must re-measure the fraction; got {row.MeanX:0.0000}.")
                .That(row.MeanX).IsCloseTo(0.25, ProbeRingOnScreenJudge.TolerancePx);

            Check.That(report.SweepRows).HasSize(ProbeRingOnScreenJudge.SweepFractions.Count);
            Check.WithCustomMessage("The offline reference noise floor must use the saved reference repeats.")
                .That(report.ReferenceNoise!.Repeats).IsEqualTo(ProbeRingOnScreenHarness.ReferenceRepeats);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 03 items 2-4, end to end: the offline report of a synthetic run carries the noise
    /// floor, an A row and every B row — assertion and sweep — each with its repeats. The lines are
    /// printed so a session can read the shape of a run's report.
    /// </summary>
    [StaFact]
    public void WriteReport_OfASyntheticRun_PrintsTheFullTable()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            var lines = new List<string>();
            ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
            foreach (var line in lines)
                _output.WriteLine(line);

            Check.That(report.Failures).IsEmpty();
            Check.WithCustomMessage(
                    "The noise floor must be reported with all five reference repeats; got: "
                    + string.Join(" | ", lines))
                .That(lines.Any(line => line.Contains("R2 noise B f=0.00") && line.Contains("N=5"))).IsTrue();
            Check.WithCustomMessage("The A rows must be reported with their repeats.")
                .That(lines.Any(line => line.Contains("R2 row A f=0.25") && line.Contains("N=3"))).IsTrue();
            Check.WithCustomMessage("Every sweep row must be reported with its repeats.")
                .That(lines.Any(line => line.Contains("R2 sweep B f=0.95") && line.Contains("N=3"))).IsTrue();
            Check.WithCustomMessage(
                    "Each row must carry one X line and one Y line; got "
                    + lines.Count(line => line.Contains("R2 sweep B f=0.50")) + " for f=0.50.")
                .That(lines.Count(line => line.Contains("R2 sweep B f=0.50"))).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 04 F4 (kills V11): the offline analyser must reproduce the live measurement field for
    /// field. The strongest available check is the printed report itself — the same line sequence —
    /// because it covers every row mean, spread, sample, half, D1 value, control number and failure.
    /// A mutant that shifts the offline expected pattern (say by 0.3 px) moves those numbers and
    /// this test goes red.
    /// </summary>
    [StaFact]
    public void Offline_EqualsLive_FieldForField()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);

            // Live: the same Measure the harness runs, over the in-memory samples.
            var referenceB = samples.First(sample =>
                sample.Kind == ProbeRingOnScreenJudge.KindReference
                && sample.Condition == ProbeRingOnScreenJudge.ConditionB
                && sample.Fraction == 0.0 && sample.Repeat == 0);
            var centreInCapture = new PhysicalPoint(
                referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
                referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY);
            var expectedBgra = ProbeRingCaptureScene.RenderCapture(
                2.0, referenceB.Look.ToSettings(), centreInCapture,
                snap: false, ProbeRingFrameSchedule.CaptureSizePx);
            var expected = ProbeRingCaptureAnalysis.ToWeights(
                expectedBgra, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx,
                referenceB.Background);
            var live = ProbeRingOnScreenJudge.Measure(
                ProbeRingOnScreenJudge.BuildInputs(2.0, expected, samples));

            var offline = ProbeRingOfflineAnalysis.Analyse(folder);

            var liveLines = new List<string>();
            ProbeRingOnScreenJudge.WriteReport(live, liveLines.Add);
            var offlineLines = new List<string>();
            ProbeRingOnScreenJudge.WriteReport(offline, offlineLines.Add);

            Check.WithCustomMessage(
                    "Offline and live reports must be identical; first difference: "
                    + FirstDifference(liveLines, offlineLines))
                .That(offlineLines.SequenceEqual(liveLines)).IsTrue();
            Check.That(offline.Failures.Count).IsEqualTo(live.Failures.Count);
            Check.That(offline.Diagnostics).HasSize(live.Diagnostics.Count);
            Check.That(offline.Sweep.ResolvedStepPx).IsEqualTo(live.Sweep.ResolvedStepPx);
            Check.That(offline.Control.Similarity).IsCloseTo(live.Control.Similarity, 1e-12);
            Check.That(offline.ReferenceOffset.MeanX).IsCloseTo(live.ReferenceOffset.MeanX, 1e-12);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 08 item 2 (kills "offline staleness rule dropped"): a saved archive carrying one
    /// byte-identical duplicate of its predecessor must mark that capture stale in a judged row —
    /// the analysis fails it with the stale-frame message rather than quietly measuring the
    /// previous frame's pixels. Run 2's own archive has exactly this shape; it predates the
    /// retakes field and the rule is re-derived from the saved bytes.
    /// </summary>
    [StaFact]
    public void Analyse_OfAnArchiveWithADuplicatedFrame_FailsTheJudgedRowAsStale()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var staleIndex = samples.FindIndex(sample =>
            sample.Kind == ProbeRingOnScreenJudge.KindAssertion
            && sample.Condition == ProbeRingOnScreenJudge.ConditionB
            && Math.Abs(sample.Fraction - 0.25) < 1e-9
            && sample.Repeat == 1);
        Check.WithCustomMessage("The fixture needs a B f=0.25 repeat 1 to duplicate.")
            .That(staleIndex).IsStrictlyGreaterThan(0);

        var predecessor = samples[staleIndex - 1];
        samples[staleIndex] = samples[staleIndex] with { Bgra = predecessor.Bgra, Weights = predecessor.Weights };

        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            Check.WithCustomMessage(
                    "The duplicated frame is staleness, not a measurement miss; got failures: "
                    + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
                .That(report.Failures.Any(failure => failure.Message.Contains("Stale frame"))).IsTrue();

            var row = report.AssertionRows.Single(candidate =>
                candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.25) < 1e-9);
            Check.WithCustomMessage("The stale repeat must be excluded from the judged mean.")
                .That(row.MeanX).IsCloseTo(0.25, ProbeRingOnScreenJudge.TolerancePx);
            Check.That(row.StaleRepeats).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 08 item 2: in the sweep a stale capture is <b>excluded from the mean and flagged</b>,
    /// not judged and not silently included — the sweep's numbers must not carry the previous
    /// frame's pixels. Kills the mutant that drops the offline rule (no flag, polluted mean).
    /// </summary>
    [StaFact]
    public void Analyse_OfAnArchiveWithAStaleSweepCapture_ExcludesItAndFlagsIt()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var staleIndex = samples.FindIndex(sample =>
            sample.Kind == ProbeRingOnScreenJudge.KindSweep
            && Math.Abs(sample.Fraction - 0.30) < 1e-9
            && sample.Repeat == 1);
        Check.WithCustomMessage("The fixture needs a sweep f=0.30 repeat 1 to duplicate.")
            .That(staleIndex).IsStrictlyGreaterThan(0);

        var predecessor = samples[staleIndex - 1];
        samples[staleIndex] = samples[staleIndex] with { Bgra = predecessor.Bgra, Weights = predecessor.Weights };

        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            var row = report.SweepRows.Single(candidate => Math.Abs(candidate.Fraction - 0.30) < 1e-9);
            Check.That(row.StaleRepeats).IsEqualTo(1);
            Check.WithCustomMessage($"The stale repeat must be excluded from the sweep mean; got {row.MeanX:0.0000}.")
                .That(row.MeanX).IsCloseTo(0.30, ProbeRingOnScreenJudge.TolerancePx);

            var lines = new List<string>();
            ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
            Check.WithCustomMessage("The sweep row must flag the excluded stale repeat.")
                .That(lines.Any(line => line.Contains("R2 sweep B f=0.30") && line.Contains("stale=1"))).IsTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 08 item 3, skip honesty by fixup 09 nit 1: the orchestrator's offline check. Opt-in on
    /// <c>EYECTRL_R2_ARCHIVE=&lt;folder&gt;</c> — a different variable from
    /// <c>EYECTRL_UI_TESTS</c> — this runs the full analyser (new judge) over a preserved archive
    /// and prints the complete report; it asserts nothing beyond completing, shows no window, and
    /// carries no <c>Category=UI</c> trait. With the variable unset it reports a <b>skip</b>, not a
    /// pass, so nobody mistakes the inert run for a verified archive.
    /// </summary>
    [StaFact]
    public void OfflineCheck_OnTheOrchestratorsArchive_PrintsTheFullReport()
    {
        var folder = Environment.GetEnvironmentVariable("EYECTRL_R2_ARCHIVE");
        if (string.IsNullOrWhiteSpace(folder))
            Assert.Skip("EYECTRL_R2_ARCHIVE is not set: this opt-in offline check needs a preserved run folder.");

        var report = ProbeRingOfflineAnalysis.Analyse(folder);
        ProbeRingOnScreenJudge.WriteReport(report, _output.WriteLine);
    }

    /// <summary>
    /// Fixup 10 item 2 (kills "offline ignores intrusion"): a saved archive carrying an overlay
    /// patch over the background must mark that capture intruded, and a judged row must fail with
    /// the overlay message — the measurement must never read through it.
    /// </summary>
    [StaFact]
    public void Analyse_OfAnArchiveWithAnIntrusion_FailsTheJudgedRowWithTheOverlayMessage()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var index = samples.FindIndex(sample =>
            sample.Kind == ProbeRingOnScreenJudge.KindAssertion
            && sample.Condition == ProbeRingOnScreenJudge.ConditionB
            && Math.Abs(sample.Fraction - 0.25) < 1e-9
            && sample.Repeat == 1);
        Check.WithCustomMessage("The fixture needs a B f=0.25 repeat 1 to patch.")
            .That(index).IsStrictlyGreaterThan(0);
        samples[index] = WithPatchedBackground(samples[index]);

        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            Check.WithCustomMessage(
                    "The overlay is an intrusion, not a measurement miss; got failures: "
                    + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
                .That(report.Failures.Any(failure => failure.Message.Contains("an overlay"))).IsTrue();

            var row = report.AssertionRows.Single(candidate =>
                candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.25) < 1e-9);
            Check.That(row.IntrudedRepeats).IsEqualTo(1);
            Check.WithCustomMessage("The intruded repeat must be excluded from the judged mean.")
                .That(row.MeanX).IsCloseTo(0.25, ProbeRingOnScreenJudge.TolerancePx);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 10 item 2: in the sweep an intruded capture is excluded from the mean and flagged
    /// <c>intruded=N</c> — not judged and not measured through.
    /// </summary>
    [StaFact]
    public void Analyse_OfAnArchiveWithAnIntrudedSweepCapture_ExcludesItAndFlagsIt()
    {
        var samples = ProbeRingSyntheticRun.Healthy();
        var index = samples.FindIndex(sample =>
            sample.Kind == ProbeRingOnScreenJudge.KindSweep
            && Math.Abs(sample.Fraction - 0.30) < 1e-9
            && sample.Repeat == 1);
        Check.WithCustomMessage("The fixture needs a sweep f=0.30 repeat 1 to patch.")
            .That(index).IsStrictlyGreaterThan(0);
        samples[index] = WithPatchedBackground(samples[index]);

        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            var row = report.SweepRows.Single(candidate => Math.Abs(candidate.Fraction - 0.30) < 1e-9);
            Check.That(row.IntrudedRepeats).IsEqualTo(1);
            Check.WithCustomMessage($"The intruded repeat must be excluded from the sweep mean; got {row.MeanX:0.0000}.")
                .That(row.MeanX).IsCloseTo(0.30, ProbeRingOnScreenJudge.TolerancePx);
            Check.WithCustomMessage("The patch is in the sweep only: nothing judged may fail.")
                .That(report.Failures).IsEmpty();

            var lines = new List<string>();
            ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
            Check.WithCustomMessage("The sweep row must flag the excluded intruded repeat.")
                .That(lines.Any(line => line.Contains("R2 sweep B f=0.30") && line.Contains("intruded=1"))).IsTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 10 item 2, opt-in on the archives (both are read-only; the test copies before it
    /// touches them): run 2's clean archive must have ZERO intruded frames — its 103 frames read 0
    /// px above every tolerance tried (10–50) — so the chosen 20 px / 200 px threshold never fires
    /// on a clean hardware run.
    /// </summary>
    [StaFact]
    public void Run2Archive_HasNoIntrudedFrames()
    {
        var folder = Environment.GetEnvironmentVariable("EYECTRL_R2_ARCHIVE");
        if (string.IsNullOrWhiteSpace(folder))
            Assert.Skip("EYECTRL_R2_ARCHIVE is not set: point it at run 2's preserved archive.");

        var copy = ProbeRingSyntheticRun.TempFolder();
        try
        {
            CopyFolder(folder, copy);
            var archive = ProbeRingCaptureArchive.Load(copy);
            var marked = ProbeRingIntrusion.Mark(archive.Samples, archive.Scale);

            Check.WithCustomMessage(
                    $"Run 2's clean frames must never be flagged intruded; got {marked.Count(sample => sample.Intruded)}.")
                .That(marked.Any(sample => sample.Intruded)).IsFalse();
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// Fixup 10 item 2, opt-in: run 3's archive must flag its overlay frames (measured: 19 of 103,
    /// the sweep pass-0 frames at f = 0.40–0.60 plus D2 and D3) and none of its judged frames
    /// (every assertion and reference frame in run 3 was byte-identical to run 2's, and the black
    /// first frame is self-consistent, so it is the control's case, not intrusion).
    /// </summary>
    [StaFact]
    public void Run3Archive_FlagsTheIntrudedFrames_AndNoneJudged()
    {
        var folder = Environment.GetEnvironmentVariable("EYECTRL_R2_ARCHIVE_RUN3");
        if (string.IsNullOrWhiteSpace(folder))
            Assert.Skip("EYECTRL_R2_ARCHIVE_RUN3 is not set: point it at run 3's preserved archive.");

        var copy = ProbeRingSyntheticRun.TempFolder();
        try
        {
            CopyFolder(folder, copy);
            var archive = ProbeRingCaptureArchive.Load(copy);
            var marked = ProbeRingIntrusion.Mark(archive.Samples, archive.Scale);

            var flagged = marked.Where(sample => sample.Intruded).ToList();
            Check.WithCustomMessage("Run 3's overlay frames must be flagged; got none.")
                .That(flagged).Not.IsEmpty();
            Check.WithCustomMessage(
                    "Run 3's judged frames were clean: nothing assertion or reference may be flagged; got "
                    + string.Join(", ", flagged
                        .Where(sample => sample.Kind is "assertion" or "reference")
                        .Select(sample => $"{sample.Kind} {sample.Condition} f={sample.Fraction:0.00} r={sample.Repeat}")))
                .That(flagged.All(sample => sample.Kind is not ("assertion" or "reference"))).IsTrue();
            Check.WithCustomMessage("The sweep pass-0 frames at f = 0.40–0.60 are the ones run 3's overlay crossed.")
                .That(flagged.Any(sample => sample.Kind == "sweep" && sample.Repeat == 0
                    && sample.Fraction >= 0.40 - 1e-9 && sample.Fraction <= 0.60 + 1e-9)).IsTrue();
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static ProbeRingCapturedSample WithPatchedBackground(ProbeRingCapturedSample sample)
    {
        var bgra = (byte[])sample.Bgra.Clone();
        for (var y = 30; y < 50; y++)
        {
            for (var x = 30; x < 60; x++)
            {
                var p = (y * ProbeRingFrameSchedule.CaptureSizePx + x) * 4;
                bgra[p] = (byte)(bgra[p] / 2);
                bgra[p + 1] = 128;
                bgra[p + 2] = 255;
            }
        }

        return sample with { Bgra = bgra };
    }

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static string FirstDifference(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        for (var i = 0; i < Math.Min(first.Count, second.Count); i++)
        {
            if (first[i] != second[i])
                return $"line {i}:\n  live    {first[i]}\n  offline {second[i]}";
        }

        return first.Count == second.Count ? "none" : $"lengths {first.Count} vs {second.Count}";
    }

    /// <summary>
    /// Fixup 05 P3: the D3 decider's rows are captured at (f, 0), so its Y expectation must be 0 —
    /// a mutant that reuses f would print a false "-f" Y error and mislead the reading.
    /// </summary>
    [StaFact]
    public void Analyse_GivesTheXOnlyDeciderItsZeroYExpectation()
    {
        var samples = ProbeRingSyntheticRun.Small();
        samples.Add(ProbeRingCaptureScene.SyntheticSample(
            ProbeRingOnScreenJudge.KindXOnly, "B", 0.0, 0.0, snap: false));
        samples.Add(ProbeRingCaptureScene.SyntheticSample(
            ProbeRingOnScreenJudge.KindXOnly, "B", 0.25, 0.0, snap: false));

        var folder = ProbeRingSyntheticRun.TempFolder();
        try
        {
            ProbeRingCaptureArchive.Save(folder, samples, 2.0, DateTimeOffset.UtcNow);
            var report = ProbeRingOfflineAnalysis.Analyse(folder);

            var d3 = report.Diagnostics.Single(diagnostic => diagnostic.Id == "D3");
            Check.That(d3.Row).IsNotNull();
            Check.That(d3.Row!.ExpectedX).IsCloseTo(0.25, 1e-9);
            Check.WithCustomMessage(
                    $"D3's rows are captured at (f, 0): its Y expectation must be 0, not f; got {d3.Row.ExpectedY}.")
                .That(d3.Row.ExpectedY).IsEqualTo(0.0);
            Check.That(d3.Row.MeanY).IsCloseTo(0.0, ProbeRingOnScreenJudge.TolerancePx);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
