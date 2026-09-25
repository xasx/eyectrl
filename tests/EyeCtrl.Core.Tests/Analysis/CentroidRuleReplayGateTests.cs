namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 3 acceptance through ticket 2's committed instrument: the production rule
/// (<see cref="ProductionFixationRule"/>) scored by <see cref="FixationReplayGates"/>, against the frozen
/// <see cref="LegacyFixationRule"/> baseline.
/// <list type="bullet">
/// <item>On the committed fixture, the same-place-gap run (846 ms capped vs 1180 ms uncapped) must stay
/// non-activatable for the production rule, and the rule's row is pinned so a drift in the ledger shows
/// up in CI without the recording;</item>
/// <item>on the real recording, the gate table is produced at r60/r75/r90, and the acceptance bars are
/// asserted at the shipped r75 (G1 ≥ 75/60 %, G2 ≤ 5 %, G4 ≤ +25 % vs legacy r75, G5 not lower,
/// G6 ≤ 2 s; G3 reported).</item>
/// </list>
/// The env-var-gated test skips cleanly when the frozen snapshot copy is absent.
/// </summary>
public class CentroidRuleReplayGateTests(ITestOutputHelper output)
{
    /// <summary>The recording is not in the repo. Point this at the frozen snapshot COPY.</summary>
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    // ── Committed fixture (always runs) ───────────────────────────────────────

    [Fact]
    public void SyntheticFixture_ProductionRule_PinsItsRow_AndKillsTheUncappedCreditScenario()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var legacy = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR75);
        var centroid = FixationReplayGates.Analyze(recording, FixationReplayGates.CentroidR75);
        output.WriteLine(FixationReplayGates.BuildReport("fixture (r75)", [
            new FixationReplayGates.ReportRow(legacy, legacy),
            new FixationReplayGates.ReportRow(centroid, legacy)]));

        // The fixture's S9 — a 0.39 s hold, a 420 ms gap, another 0.39 s hold at the same place — accrues
        // 846 ms capped and 1200 ms uncapped. A capped run at ~846 ms must exist and must NOT be
        // activatable; uncap the per-sample dt (FixationDetector) and the run's credited age jumps to
        // 1200 ms, so the Single below finds nothing and the test reds.
        var cappedGapRuns = centroid.Runs.Where(r => r.CreditedAgeUs is > 800_000 and < 900_000).ToList();
        Check.That(cappedGapRuns.Any(r => r.CreditedAgeUs == 846_000)).IsTrue(); // S9's capped ledger
        Check.That(cappedGapRuns).ContainsOnlyElementsThatMatch(r => !r.IsActivatable);

        // The pinned production row for the fixture. Same stream, same instrument as the legacy row above.
        // The one "elsewhere" episode is the fixture's S5 (a real in-window fixation in the wrong place),
        // which the rule must report rather than hide.
        AssertRow(centroid, centroid.At150, episodes: 9, served: 5, elsewhere: 1, midas: 0);
        AssertRow(centroid, centroid.At200, episodes: 9, served: 5, elsewhere: 1, midas: 0);
        Check.That(centroid.Runs).HasSize(13);
        Check.That(centroid.ActivatableRuns).IsEqualTo(8);
        Check.That(centroid.KeyboardRuns).IsEqualTo(13);
        Check.That(centroid.EndedRuns).IsEqualTo(12);
    }

    [Fact]
    public void ProductionAndHarness_AgreeOnEveryPinnedConstant()
    {
        // The frozen LegacyFixationRule is "today's rule under ticket 3's clock", so its ledger constants
        // must equal the production rule's — otherwise the baseline silently measures a different clock
        // from the candidate. The DwellSettings defaults are part of the same contract (fixup-01 F5): the
        // engine reads THEM while the harness pins its own copies, and a drift would leave the gates
        // scoring a rule the app no longer runs. Drift guard, no behaviour.
        Check.That(FixationDetector.AgeCreditCapMs).IsEqualTo(FixationReplayGates.AgeCreditCapMs);
        Check.That(FixationDetector.GapAbortMs).IsEqualTo(FixationReplayGates.EpisodeGapBreakMs);
        var dwell = new DwellSettings();
        Check.That(dwell.FixationDetectionMs).IsEqualTo(FixationReplayGates.FixationDetectionMs);
        Check.That(dwell.ActivationButtonDwellMs).IsEqualTo(FixationReplayGates.ActivationButtonDwellMs);
        Check.That(dwell.DwellCooldownMs).IsEqualTo(FixationReplayGates.ActivationCooldownMs);
        Check.That(dwell.FixationRadiusPixels).IsEqualTo(75.0);
    }

    // ── Real recording (env-gated) ────────────────────────────────────────────

    [Fact]
    public void RealRecording_ProductionRule_GateTable_AndAcceptanceBars()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy "
            + "(the live file is appended to and rotated).");

        var recording = GazeRecordingFile.Read(path!);
        Check.That(recording.MalformedLines).IsEqualTo(0);

        var legacy50 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR50);
        var legacy60 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayAtRadius(60));
        var legacy75 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR75);
        var legacy90 = FixationReplayGates.Analyze(recording, FixationReplayGates.TodayR90);

        var centroid60 = FixationReplayGates.Analyze(recording, FixationReplayGates.CentroidR60);
        var centroid75 = FixationReplayGates.Analyze(recording, FixationReplayGates.CentroidR75);
        var centroid90 = FixationReplayGates.Analyze(recording, FixationReplayGates.CentroidR90);

        output.WriteLine(FixationReplayGates.BuildReport(Path.GetFileName(path!), [
            new FixationReplayGates.ReportRow(legacy50, legacy50),
            new FixationReplayGates.ReportRow(centroid60, legacy60),
            new FixationReplayGates.ReportRow(centroid75, legacy75),
            new FixationReplayGates.ReportRow(centroid90, legacy90)]));
        foreach (var (analysis, baseline) in new[]
        {
            (centroid60, legacy60), (centroid75, legacy75), (centroid90, legacy90),
        })
        {
            output.WriteLine($"{analysis.Candidate.Name} verdicts:");
            foreach (var verdict in FixationReplayGates.EvaluateGates(analysis, baseline))
                output.WriteLine($"  {verdict.Gate}: {(verdict.Passed switch { true => "pass", false => "FAIL", null => "n/a" })} — {verdict.Detail}");
        }

        // Acceptance (ticket 3, "Replay gates"): the shipped r75 passes G1 at both bars and G2.
        Check.That(centroid75.At150.ServedPasses).IsTrue();
        Check.That(centroid75.At200.ServedPasses).IsTrue();
        Check.That(centroid75.At150.ElsewherePasses).IsTrue();
        Check.That(centroid75.At200.ElsewherePasses).IsTrue();

        var verdicts75 = FixationReplayGates.EvaluateGates(centroid75, legacy75);
        Check.That(verdicts75.Single(v => v.Gate == "G4").Passed).IsEqualTo(true);
        Check.That(verdicts75.Single(v => v.Gate == "G5").Passed).IsEqualTo(true);
        Check.That(verdicts75.Single(v => v.Gate == "G6").Passed).IsEqualTo(true);

        // Determinism: the same input gives the same row.
        var rerun = FixationReplayGates.Analyze(recording, FixationReplayGates.CentroidR75);
        Check.That(rerun.At150.ServedEpisodes).IsEqualTo(centroid75.At150.ServedEpisodes);
        Check.That(rerun.ActivatableRuns).IsEqualTo(centroid75.ActivatableRuns);
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
