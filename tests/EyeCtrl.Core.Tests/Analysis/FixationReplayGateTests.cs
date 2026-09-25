namespace EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Teeth for the committed fixation-gate instrument. The synthetic fixture's geometry is known by
/// construction (two clean holds whose anchors sit 40 px off their episode median, plus one slow ramp
/// that is a held episode at D200 but never becomes an activatable fixation), so every gate number is
/// checkable by hand rather than only self-consistent.
/// </summary>
public class FixationReplayGateTests(ITestOutputHelper output)
{
    /// <summary>The recording is not in the repo. Point this at a COPY of the user's file.</summary>
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    // ── Synthetic fixture ─────────────────────────────────────────────────────

    [Fact]
    public void SyntheticFixture_AtKnownHeldEpisodes_ProducesKnownG1()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        Check.That(recording.UnusableReason).IsNull();   // committed with its trailer: a clean file
        Check.That(recording.Samples).HasSize(220);

        var analysis = FixationReplayGates.Analyze(
            recording.Samples, FixationReplayGates.TodayR50, recording.Header.CalibrationTransform);

        // Three held episodes at each dispersion: two clean holds (anchors deliberately 40 px off
        // their episode median) and the alternation segment, which is held and never served.
        Check.That(analysis.At150.EpisodeCount).IsEqualTo(3);
        Check.That(analysis.At150.ServedEpisodes).IsEqualTo(2);
        Check.That(analysis.At150.ServedShare).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(analysis.At150.MidasRuns).IsEqualTo(0);
        Check.That(analysis.At150.ElsewhereEpisodes).IsEqualTo(0);

        Check.That(analysis.At200.EpisodeCount).IsEqualTo(3);
        Check.That(analysis.At200.ServedEpisodes).IsEqualTo(2);
        Check.That(analysis.At200.ServedShare).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(analysis.At200.MidasRuns).IsEqualTo(0);
        Check.That(analysis.At200.ElsewhereEpisodes).IsEqualTo(0);

        // Exactly the two real holds are activatable, and nothing else ever fixates.
        Check.That(analysis.ActivatableRuns).IsEqualTo(2);
        Check.That(analysis.KeyboardRuns).IsEqualTo(2);
        Check.That(analysis.EndedRuns).IsEqualTo(2);

        // The first hold's onset fires one detection time after the stream starts.
        Check.That(analysis.TimeToFirstFixationSeconds!.Value).IsCloseTo(0.30, 0.05);
    }

    [Fact]
    public void SyntheticFixture_ServedBound_IsWhatMakesTheEpisodesServed()
    {
        // The named mutation, modeled in-process: shrink the served@place bound to 1 px and the two
        // 40 px-eccentric anchors can no longer serve anything. If a future edit stopped reading the
        // bound, both halves of this test would fail together.
        var recording = GazeRecordingFile.Read(FixturePath);
        var pinned = FixationReplayGates.Analyze(
            recording.Samples, FixationReplayGates.TodayR50, recording.Header.CalibrationTransform);
        var shifted = FixationReplayGates.Analyze(
            recording.Samples, FixationReplayGates.TodayR50, recording.Header.CalibrationTransform,
            servedDistancePx: 1.0);

        Check.That(pinned.At150.ServedShare).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(shifted.At150.ServedEpisodes).IsEqualTo(0);
        Check.That(shifted.At150.ServedShare).IsEqualTo(0.0);
        Check.That(shifted.At200.ServedEpisodes).IsEqualTo(0);
        Check.That(shifted.At200.ServedShare).IsEqualTo(0.0);

        // The episodes themselves are untouched: only their service verdict moved.
        Check.That(shifted.At150.EpisodeCount).IsEqualTo(pinned.At150.EpisodeCount);
        Check.That(shifted.At200.EpisodeCount).IsEqualTo(pinned.At200.EpisodeCount);
    }

    [Fact]
    public void SyntheticFixture_ReplayIsDeterministic_OneTableTwice()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var first = FixationReplayGates.BuildReport("fixture", [
            FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR50),
            FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR90)]);
        var second = FixationReplayGates.BuildReport("fixture", [
            FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR50),
            FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR90)]);

        Check.That(first).IsEqualTo(second);
        Check.That(first).Contains("today's rule, r50");
    }

    // ── Real recording ────────────────────────────────────────────────────────

    [Fact]
    public void RealRecording_BaselineAtR50AndR90_AndTodayFailsG1()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Copy %APPDATA%/EyeCtrl/gaze-recording.csv aside (the recorder appends "
            + "to and rotates the original) and set " + RecordingEnvironmentVariable + " to the copy.");

        var recording = GazeRecordingFile.Read(path!);
        Check.That(recording.Samples).Not.IsEmpty();

        var r50 = FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR50, recording.Header.CalibrationTransform);
        var r90 = FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR90, recording.Header.CalibrationTransform);
        var report = FixationReplayGates.BuildReport(Path.GetFileName(path!), [r50, r90]);
        output.WriteLine(report);

        // The gate's own guard, from the ticket: the "new gates fail on old code" check. Today's rule
        // at r90 must miss G1 at BOTH dispersions, or the gate cannot separate the candidate rule.
        Check.That(r90.At150.ServedPasses).IsFalse();
        Check.That(r90.At200.ServedPasses).IsFalse();

        // Determinism on the real stream: the pinned numbers below are re-derivable exactly.
        var rerun = FixationReplayGates.Analyze(recording.Samples, FixationReplayGates.TodayR90, recording.Header.CalibrationTransform);
        Check.That(rerun.ActivatableRuns).IsEqualTo(r90.ActivatableRuns);
        Check.That(rerun.At150.ServedEpisodes).IsEqualTo(r90.At150.ServedEpisodes);
        Check.That(rerun.At150.MidasRuns).IsEqualTo(r90.At150.MidasRuns);

        // The debate's expected baseline, measured by its real-Core replay: M1 3.1 / 7.9 per minute,
        // M4 47.0 / 54.5. These assertions pin THIS harness's numbers; if they ever disagree with the
        // debate's, the report says so rather than bending the instrument.
        Check.That(r50.ActivatablePerMinute).IsCloseTo(3.1, 0.05);
        Check.That(r90.ActivatablePerMinute).IsCloseTo(7.9, 0.05);
        Check.That(r50.FlickerPerMinute).IsCloseTo(47.0, 0.05);
        Check.That(r90.FlickerPerMinute).IsCloseTo(54.5, 0.05);
    }
}
