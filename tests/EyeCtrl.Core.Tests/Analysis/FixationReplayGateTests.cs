namespace EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Teeth for the committed fixation-gate instrument. The synthetic fixture carries one scenario per
/// mutation the gate must kill, and every scenario is checkable by hand:
/// <list type="bullet">
/// <item>S1/S2 — clean 2.49 s holds whose anchors sit 40 px off their episode median: served; the
/// 40 px eccentricity makes the served@place bound load-bearing.</item>
/// <item>S3 — a 1.17 s alternation that is a held episode at D150 and D200 and never a fixation:
/// an unserved episode, so G1 cannot be 100 % by construction.</item>
/// <item>S4 — a 0.87 s hold (M2, not M1), a 450 ms gap, then a 2.19 s hold 130 px away whose onset
/// lands 1.62 s into the episode: served only if the onset window is unbounded.</item>
/// <item>S5 — a 1.2 s hold at 700 then a 3 s hold at 840: the only in-window fixation is misplaced,
/// so the episode is "elsewhere".</item>
/// <item>S6 — two 0.78 s holds 600 ms apart: no episode unless the gap-break rule is removed, and a
/// run that starts in no episode span (a real Midas case).</item>
/// <item>S7 — credited age 990 ms: must NOT activate (kills activation one frame early).</item>
/// <item>S8 — credited age exactly 1000 ms: must activate (pins the boundary itself).</item>
/// <item>S9 — 0.39 s hold + 420 ms gap + 0.39 s hold = 846 ms capped vs 1200 ms uncapped: must NOT
/// activate (kills uncapped per-sample credit).</item>
/// <item>S10 — a same-place gap spanning the 300 ms onset: the engine parity test's P2 killer.</item>
/// <item>S11 — a final hold still open when the stream ends: dropping open runs must change a count.</item>
/// </list>
/// </summary>
public class FixationReplayGateTests(ITestOutputHelper output)
{
    /// <summary>The recording is not in the repo. Point this at the frozen snapshot COPY.</summary>
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    // ── Synthetic fixture ─────────────────────────────────────────────────────

    [Fact]
    public void SyntheticFixture_PinsEveryGate()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        Check.That(recording.UnusableReason).IsNull();   // committed with its trailer: a clean file
        Check.That(recording.Samples).HasSize(842);

        var analysis = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);

        // Nine episodes at both dispersions: S1, S2, S3 (alternation), S4, S5, S8, S9, S10 and S11.
        // S6's pieces are 0.78 s each (an episode only if the >500 ms gap-break rule is removed), S7's
        // hold is 0.99 s (just under the one-second minimum).
        Check.That(analysis.At150.EpisodeCount).IsEqualTo(9);
        Check.That(analysis.At150.ServedEpisodes).IsEqualTo(5);
        Check.That(analysis.At150.ServedShare).IsCloseTo(5.0 / 9.0, 1e-9);
        Check.That(analysis.At150.ElsewhereEpisodes).IsEqualTo(1);
        Check.That(analysis.At150.MidasRuns).IsEqualTo(1);

        Check.That(analysis.At200.EpisodeCount).IsEqualTo(9);
        Check.That(analysis.At200.ServedEpisodes).IsEqualTo(5);
        Check.That(analysis.At200.ServedShare).IsCloseTo(5.0 / 9.0, 1e-9);
        Check.That(analysis.At200.ElsewhereEpisodes).IsEqualTo(1);
        Check.That(analysis.At200.MidasRuns).IsEqualTo(1);

        // Twelve runs, nine activatable. S4's first hold (870 ms), S7 (990 ms) and S9 (846 ms capped)
        // are M2-only; S11 is still open at the end of the stream.
        Check.That(analysis.Runs).HasSize(12);
        Check.That(analysis.ActivatableRuns).IsEqualTo(9);
        Check.That(analysis.KeyboardRuns).IsEqualTo(12);
        Check.That(analysis.EndedRuns).IsEqualTo(11);

        Check.That(analysis.TimeToFirstFixationSeconds!.Value).IsCloseTo(0.30, 0.05);
    }

    [Fact]
    public void SyntheticFixture_ServedBound_IsWhatMakesTheEccentricAnchorsServe()
    {
        // The served@place bound, moved in-process: at 1 px the two 40 px-eccentric anchors (S1/S2)
        // can no longer serve, and only the near-centred holds (S8, S10, S11) remain. If the metric
        // stopped reading the bound, both halves would fail together.
        var recording = GazeRecordingFile.Read(FixturePath);
        var pinned = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);
        var shifted = FixationReplayGates.Analyze(
            recording, FixationReplayGates.TodayR50, servedDistancePx: 1.0);

        Check.That(pinned.At150.ServedEpisodes).IsEqualTo(5);
        Check.That(shifted.At150.ServedEpisodes).IsStrictlyLessThan(pinned.At150.ServedEpisodes);
        Check.That(shifted.At200.ServedEpisodes).IsStrictlyLessThan(pinned.At200.ServedEpisodes);

        // The episodes themselves are untouched: only their service verdict moved.
        Check.That(shifted.At150.EpisodeCount).IsEqualTo(pinned.At150.EpisodeCount);
        Check.That(shifted.At200.EpisodeCount).IsEqualTo(pinned.At200.EpisodeCount);
    }

    [Fact]
    public void SyntheticFixture_ReplayIsDeterministic_OneTableTwice()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var first = FixationReplayGates.BuildReport("fixture", [
            FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50),
            FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR90)]);
        var second = FixationReplayGates.BuildReport("fixture", [
            FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50),
            FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR90)]);

        Check.That(first).IsEqualTo(second);
        Check.That(first).Contains("today's rule, r50");
    }

    // ── Rule-seam hardening ───────────────────────────────────────────────────

    [Fact]
    public void DetectRuns_FailsLoudly_WhenAnAdapterDropsOrDuplicatesRuns()
    {
        // A buggy adapter that double-starts or never releases would silently drop runs, and dropped
        // runs UNDER-count flicker — which makes G4 pass spuriously. Both must throw.
        var frames = new[]
        {
            Frame(100, 100, 1_000_000),
            Frame(100, 100, 1_030_000),
            Frame(100, 100, 1_060_000),
        };

        var doubleStarter = new BuggyRule();
        doubleStarter.OnUpdate = (rule, point, ts) =>
        {
            rule.RaiseStarted(point, ts);
            rule.RaiseStarted(point, ts);
        };
        InvalidOperationException? doubleStartEx = null;
        try { FixationReplayGates.DetectRuns(frames, CandidateFor(doubleStarter)); }
        catch (InvalidOperationException ex) { doubleStartEx = ex; }
        Check.That(doubleStartEx).IsNotNull();

        var neverReleases = new BuggyRule();
        neverReleases.OnUpdate = (rule, point, ts) => rule.RaiseStarted(point, ts);
        InvalidOperationException? noReleaseEx = null;
        try { FixationReplayGates.DetectRuns(frames, CandidateFor(neverReleases)); }
        catch (InvalidOperationException ex) { noReleaseEx = ex; }
        Check.That(noReleaseEx).IsNotNull();
    }

    private static ReplayPipeline.Frame Frame(double x, double y, long timestampUs) =>
        new(new EyeCtrl.Core.Models.GazePoint(x, y, timestampUs, true),
            new EyeCtrl.Core.Models.PhysicalPoint(x, y),
            new EyeCtrl.Core.Models.PhysicalPoint(x, y),
            IsInGap: false);

    private static FixationReplayGates.Candidate CandidateFor(IFixationRule rule) =>
        new("buggy adapter", () => rule, () => new EyeCtrl.Core.Processing.OneEuroFilter());

    private sealed class BuggyRule : IFixationRule
    {
        public Action<BuggyRule, EyeCtrl.Core.Models.PhysicalPoint, long>? OnUpdate { get; set; }
        public event Action<EyeCtrl.Core.Models.PhysicalPoint, long>? FixationStarted;
        public event Action<long>? FixationActivated;
        public event Action<FixationRelease>? FixationReleased;

        public void RaiseStarted(EyeCtrl.Core.Models.PhysicalPoint point, long timestampUs) =>
            FixationStarted?.Invoke(point, timestampUs);

        public void Update(EyeCtrl.Core.Models.PhysicalPoint point, long timestampUs, bool isValid) =>
            OnUpdate?.Invoke(this, point, timestampUs);

        public void CompleteStream() { }
    }

    // ── Verdicts ──────────────────────────────────────────────────────────────

    [Fact]
    public void Verdicts_WithoutABaseline_G4AndG5AreNotEvaluated()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var analysis = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);

        var verdicts = FixationReplayGates.EvaluateGates(analysis, baseline: null);

        Check.That(verdicts.Select(v => v.Gate)).ContainsOnlyElementsThatMatch(
            g => g is "G1 D150" or "G1 D200" or "G2 D150" or "G2 D200" or "G4" or "G5" or "G6");
        Check.That(verdicts.Single(v => v.Gate == "G4").Passed).IsNull();
        Check.That(verdicts.Single(v => v.Gate == "G5").Passed).IsNull();
        // G2 fails on the fixture's deliberate elsewhere episode; G6 passes.
        Check.That(verdicts.Single(v => v.Gate == "G2 D150").Passed).IsEqualTo(false);
        Check.That(verdicts.Single(v => v.Gate == "G6").Passed).IsEqualTo(true);
    }

    [Fact]
    public void Verdicts_WithABaseline_ApplyTheDeclaredCaps()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var analysis = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);

        // Same analysis as its own baseline: everything passes by construction.
        var self = FixationReplayGates.EvaluateGates(analysis, analysis);
        Check.That(self.Single(v => v.Gate == "G4").Passed).IsEqualTo(true);
        Check.That(self.Single(v => v.Gate == "G5").Passed).IsEqualTo(true);

        // Flicker beyond +25 % and keyboard below today must both fail.
        var regressing = analysis with
        {
            EndedRuns = analysis.EndedRuns * 2,
            KeyboardRuns = analysis.KeyboardRuns - 1,
            At150 = analysis.At150 with { ElsewhereEpisodes = analysis.At150.EpisodeCount },
            TimeToFirstFixationSeconds = 3.0,
        };
        var verdicts = FixationReplayGates.EvaluateGates(regressing, analysis);
        Check.That(verdicts.Single(v => v.Gate == "G4").Passed).IsEqualTo(false);
        Check.That(verdicts.Single(v => v.Gate == "G5").Passed).IsEqualTo(false);
        Check.That(verdicts.Single(v => v.Gate == "G2 D150").Passed).IsEqualTo(false);
        Check.That(verdicts.Single(v => v.Gate == "G6").Passed).IsEqualTo(false);
    }

    // ── Real recording ────────────────────────────────────────────────────────

    [Fact]
    public void RealRecording_NewBaselineRows_AndTodayFailsG1()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy "
            + "(the live file is appended to and rotated).");

        var recording = GazeRecordingFile.Read(path!);
        Check.That(recording.MalformedLines).IsEqualTo(0);

        var r50 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);
        var r75 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR75);
        var r90 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR90);
        output.WriteLine(FixationReplayGates.BuildReport(Path.GetFileName(path!), [
            new FixationReplayGates.ReportRow(r50, r50),
            new FixationReplayGates.ReportRow(r75, r75),
            new FixationReplayGates.ReportRow(r90, r90)]));

        // The "new gates fail on old code" guard: today's rule at r90 misses G1 at both dispersions.
        Check.That(r90.At150.ServedPasses).IsFalse();
        Check.That(r90.At200.ServedPasses).IsFalse();

        // Full rows, pinned: episodes, served, elsewhere, Midas; then runs, activatable, keyboard,
        // ended and time-to-first-fixation. These are the numbers ticket 3 compares against.
        AssertRow(r50, r50.At150, 78, 17, 0, 1);
        AssertRow(r50, r50.At200, 105, 16, 0, 0);
        Check.That(r50.ActivatableRuns).IsEqualTo(19);
        Check.That(r50.KeyboardRuns).IsEqualTo(48);
        Check.That(r50.EndedRuns).IsEqualTo(347);
        Check.That(r50.TimeToFirstFixationSeconds!.Value).IsCloseTo(0.967, 0.01);

        AssertRow(r75, r75.At150, 78, 33, 0, 1);
        AssertRow(r75, r75.At200, 105, 30, 0, 2);
        Check.That(r75.ActivatableRuns).IsEqualTo(36);
        Check.That(r75.KeyboardRuns).IsEqualTo(83);
        Check.That(r75.EndedRuns).IsEqualTo(381);
        Check.That(r75.TimeToFirstFixationSeconds!.Value).IsCloseTo(1.088, 0.01);

        AssertRow(r90, r90.At150, 78, 44, 0, 5);
        AssertRow(r90, r90.At200, 105, 42, 0, 4);
        Check.That(r90.ActivatableRuns).IsEqualTo(51);
        Check.That(r90.KeyboardRuns).IsEqualTo(115);
        Check.That(r90.EndedRuns).IsEqualTo(402);
        Check.That(r90.TimeToFirstFixationSeconds!.Value).IsCloseTo(0.967, 0.01);

        // Determinism on the real stream: the pinned numbers above are re-derivable exactly.
        var rerun = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR90);
        Check.That(rerun.ActivatableRuns).IsEqualTo(r90.ActivatableRuns);
        Check.That(rerun.At150.ServedEpisodes).IsEqualTo(r90.At150.ServedEpisodes);
        Check.That(rerun.At150.MidasRuns).IsEqualTo(r90.At150.MidasRuns);
    }

    private static void AssertRow(
        FixationReplayGates.FixationAnalysis analysis,
        FixationReplayGates.GateRow row,
        int episodes, int served, int elsewhere, int midas)
    {
        Check.That(row.EpisodeCount).IsEqualTo(episodes);
        Check.That(row.ServedEpisodes).IsEqualTo(served);
        Check.That(row.ElsewhereEpisodes).IsEqualTo(elsewhere);
        Check.That(row.MidasRuns).IsEqualTo(midas);
        Check.That(analysis.Candidate.Name).IsNotEmpty();
    }
}
