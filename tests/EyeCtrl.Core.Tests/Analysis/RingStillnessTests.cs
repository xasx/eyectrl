namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 4's replay acceptance, on ticket 2's committed instrument: the "ring still" share on
/// held-episode frames must rise when the ring pins.
///
/// <para>
/// The primary convention is the ticket's 15 % → 80 % one: a frame counts as STILL when the ring's
/// drawn position is within <see cref="StillTolerancePx"/> of where it was on the PREVIOUS sample —
/// the eye is holding, and the ring is not drawing the hold's own jitter. On the frozen recording
/// that reads 13.9 % for the following widget and 77.7 % for the pinned one.
/// </para>
///
/// <para>
/// WHAT THE NUMBER IS. <see cref="RingStillness"/> measures a DISPLAY model with no interaction
/// state: every rule run is pinned from onset to its last credited sample, as if the widget were
/// always on screen for the whole hold. The real app hides the ring when an activation opens the
/// action menu or the magnifier, and after that overlay closes the latched run never re-pins. So
/// 77.7 % is an UPPER BOUND. The two bounded treatments are measured and printed beside it on the
/// same convention: 69.9 % with the post-activation frames dropped (26.1 % of the held-episode
/// frames sit after their run's activation), and 56.1 % with those frames counted as following.
/// Reviewer A confirmed this split is the exact one. The advocate's 10 DIP / 200 ms convention
/// (20 px at ScaleX 2.0) reads 52.6 % → 84.2 % and is printed alongside.
/// </para>
///
/// <para>
/// The fixture rows run in CI. The real-recording row is env-gated like every other replay gate
/// (<c>EYECTRL_GAZE_RECORDING</c> points at the frozen snapshot copy; the file is not in the repo).
/// </para>
/// </summary>
public class RingStillnessTests(ITestOutputHelper output)
{
    /// <summary>The recording is not in the repo. Point this at the frozen snapshot COPY.</summary>
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";

    /// <summary>The ticket's stillness tolerance: one physical pixel of movement between samples.</summary>
    private const double StillTolerancePx = 1.0;

    /// <summary>The advocate's context row: 10 DIP at the user's ScaleX 2.0, 200 ms back.</summary>
    private const double AdvocateTolerancePx = 20.0;

    private const int AdvocateLagMs = 200;

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    private static ReplayPipeline.Frame[] Replay(GazeRecordingFile.Recording recording)
    {
        FixationReplayGates.EnsureReplayable(recording);
        return ReplayPipeline.RunSynthetic(
            recording.Samples,
            new ReplayPipeline.Candidate("retuned", () => new OneEuroFilter(), FixationReplayGates.MaxGapInterpolationMs),
            recording.Header.CalibrationTransform);
    }

    [Fact]
    public void SettlePlusGlideModel_PinsOnlyFromTheSettle_SoItsStillShareDropsBelowTheOnsetPin()
    {
        // Ticket 7's M2 prediction: pinning at settle loses the 300–500 ms of every run and every run
        // shorter than the settle, so the settle model must NOT read like the onset pin. Mutation: pin
        // at the onset in the settle model (drop the PinAtSettle check in RingStillness.IsPinned) →
        // the two pinned shares become equal and this reds.
        var recording = GazeRecordingFile.Read(FixturePath);
        var frames = Replay(recording);
        var runs = FixationReplayGates.DetectRuns(frames, FixationReplayGates.CentroidR75);
        var episodes = FixationReplayGates.ExtractHeldEpisodes(frames, FixationReplayGates.Dispersion150Px);

        var onset = RingStillness.Measure(
            frames, episodes, RingStillness.Model.Pinned(runs), StillTolerancePx, lagMs: 0);
        var settle = RingStillness.Measure(
            frames, episodes, RingStillness.Model.PinnedAtSettle(runs, glideMs: 120),
            StillTolerancePx, lagMs: 0);

        Check.That(onset.ConsideredFrames).IsEqualTo(settle.ConsideredFrames);
        Check.That(settle.PinnedShare).IsStrictlyGreaterThan(0.0);
        Check.That(settle.PinnedShare).IsStrictlyLessThan(onset.PinnedShare);
    }

    [Fact]
    public void SyntheticFixture_PinningRaisesTheStillShare_OverFollowing()
    {
        var recording = GazeRecordingFile.Read(FixturePath);
        var rows = Measure(recording, output);

        output.WriteLine("fixture @150:");
        Report(output, "following (today)", rows.Following);

        // The pinned model must hold the ring on the covered episodes, and it must beat the following
        // model by a wide margin — the display fix, not the rule, is what the stillness rides on.
        Check.That(rows.PinnedUpper.ConsideredFrames).IsEqualTo(rows.Following.ConsideredFrames);
        Check.That(rows.PinnedUpper.PinnedShare).IsStrictlyGreaterThan(0.75);
        Check.That(rows.PinnedUpper.StillShare).IsGreaterOrEqualThan(0.80);
        Check.That(rows.PinnedUpper.StillShare).IsStrictlyGreaterThan(rows.Following.StillShare);
    }

    [Fact]
    public void RealRecording_PinningRaisesTheStillShare_ToAboutEightyPercent()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy "
            + "(the live file is appended to and rotated).");

        var recording = GazeRecordingFile.Read(path!);
        var rows = Measure(recording, output);

        output.WriteLine($"{Path.GetFileName(path!)} @150:");
        Report(output, "following (today)", rows.Following);

        // Acceptance: the upper bound is about 80 % still on held-episode frames (from about 15 %), on
        // the shipped r75 rule. The bars carry slack because this is a display measurement on one 443 s
        // recording, not a product gate.
        Check.That(rows.PinnedUpper.StillShare).IsGreaterOrEqualThan(0.70);
        Check.That(rows.Following.StillShare).IsLessOrEqualThan(0.20);
        Check.That(rows.PinnedUpper.PinnedShare).IsGreaterOrEqualThan(0.75);
    }

    private sealed record Rows(
        RingStillness.Measurement Following,
        RingStillness.Measurement PinnedUpper,
        RingStillness.Measurement PinnedDropped,
        RingStillness.Measurement PinnedFollowing);

    /// <summary>
    /// Measures the following model and the pinned model under each <see cref="RingStillness.PostActivation"/>
    /// treatment, on one recording: the primary 1 px / previous-sample row the acceptance uses, the
    /// upper-bound/bounded pinned rows, and the advocate's 10 DIP / 200 ms context row.
    /// </summary>
    private static Rows Measure(GazeRecordingFile.Recording recording, ITestOutputHelper output)
    {
        var frames = Replay(recording);
        var runs = FixationReplayGates.DetectRuns(frames, FixationReplayGates.CentroidR75);
        var episodes = FixationReplayGates.ExtractHeldEpisodes(frames, FixationReplayGates.Dispersion150Px);
        var pinned = RingStillness.Model.Pinned(runs);

        var following = RingStillness.Measure(
            frames, episodes, RingStillness.Model.Following, StillTolerancePx, lagMs: 0);
        var upper = RingStillness.Measure(frames, episodes, pinned, StillTolerancePx, lagMs: 0);
        var dropped = RingStillness.Measure(
            frames, episodes, pinned, StillTolerancePx, lagMs: 0, RingStillness.PostActivation.Dropped);
        var follows = RingStillness.Measure(
            frames, episodes, pinned, StillTolerancePx, lagMs: 0, RingStillness.PostActivation.Following);

        Report(output, "pinned (upper bound)", upper);
        Report(output, "pinned, post-activation dropped", dropped);
        Report(output, "pinned, post-activation follows", follows);

        var followingAdvocate = RingStillness.Measure(
            frames, episodes, RingStillness.Model.Following, AdvocateTolerancePx, AdvocateLagMs);
        var upperAdvocate = RingStillness.Measure(
            frames, episodes, pinned, AdvocateTolerancePx, AdvocateLagMs);
        Report(output, "advocate following", followingAdvocate);
        Report(output, "advocate pinned", upperAdvocate);

        return new Rows(following, upper, dropped, follows);
    }

    private static void Report(ITestOutputHelper output, string name, RingStillness.Measurement m) =>
        output.WriteLine(
            $"  {name,-34} still {m.StillShare:P1}, pinned {m.PinnedShare:P1}, frames {m.ConsideredFrames}");
}
