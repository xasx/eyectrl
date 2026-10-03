namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Ticket 7, part D: produces the replay report. The real-recording row is env-gated like every other
/// replay gate (<c>EYECTRL_GAZE_RECORDING</c> points at the frozen snapshot copy; the file is not in
/// the repo) and, when <c>EYECTRL_REPLAY_REPORT_OUT</c> names a path, writes the markdown there for
/// the report artifact. The fixture rows keep the whole analysis running in CI.
/// </summary>
public class ActivationReplayReportTests(ITestOutputHelper output)
{
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";
    private const string ReportOutputEnvironmentVariable = "EYECTRL_REPLAY_REPORT_OUT";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    [Fact]
    public void SyntheticFixture_ReportIsProduced_AndCarriesEverySection()
    {
        // The CI-visible half: the whole walk (rule, settle, engine, corner counts, lever grids) runs on
        // the committed fixture, so a break in the analysis is not hidden behind the env gate.
        var recording = GazeRecordingFile.Read(FixturePath);

        var markdown = ActivationReplayReport.Build(
            recording, FixturePath, ActivationReplayReport.Inputs.UserGeometry());

        Check.That(markdown).Contains("R90 (the user's)");
        Check.That(markdown).Contains("R75 (ticket 3's default)");
        Check.That(markdown).Contains("G3′");
        Check.That(markdown).Contains("the hold after the settle");
        Check.That(markdown).Contains("the 1000 ms linger");
        Check.That(markdown).Contains("Near-activations");
        Check.That(markdown).Contains("The caveat is two-sided");
        Check.That(markdown).Contains("## The top corners");
        Check.That(markdown).Contains("## Levers");
        Check.That(markdown).Contains("**Linger**");
        Check.That(markdown).Contains("**Settle time**");
        Check.That(markdown).Contains("**Hit radius**");
        Check.That(markdown).Contains("Looks that still found a button");
        Check.That(markdown).Contains("## Coordinator's reading (not a decision)");
        Check.That(markdown).Contains("## Ring still share");
        Check.That(markdown).Contains("Following, before ticket 4");
        Check.That(markdown).Contains("every run that can be offered");
    }

    [Fact]
    public void TheLingerLever_MovesTheOnScreenShare_OnTheSameStream()
    {
        // Review B F2: the linger is a lever because the engine's expiry is an input, not a const.
        // Mutation: ignore the settable expiry in the engine (use DefaultOfferExpiryMs) → the two
        // shares are equal and this reds.
        var recording = GazeRecordingFile.Read(FixturePath);
        var inputs = ActivationReplayReport.Inputs.UserGeometry();

        var longLinger = ActivationReplayReport.Headline(recording, inputs.WithExpiry(1000));
        var shortLinger = ActivationReplayReport.Headline(recording, inputs.WithExpiry(400));

        Check.That(longLinger.OnScreenShare).IsStrictlyGreaterThan(shortLinger.OnScreenShare);
        Check.That(shortLinger.OnScreenShare).IsStrictlyGreaterThan(0.0);
        // Pop-ups are the same offers either way: the linger moves their life, not their count.
        Check.That(shortLinger.PopUpsPerMinute).IsEqualTo(longLinger.PopUpsPerMinute);
    }

    [Fact]
    public void Poisson95Range_ReproducesTheReviewedExactValues()
    {
        // Review B F3 quoted 1 event in 7.38 min as 0.003–0.75/min. Mutation: solve the upper bound for
        // P(X ≥ count) instead of P(X ≥ count + 1) → the upper bound collapses toward the lower one.
        var range = ActivationReplayReport.Poisson95Range(1, 7.382);

        Check.That(range.Lower).IsCloseTo(0.00343, 1e-5);
        Check.That(range.Upper).IsCloseTo(0.755, 1e-3);
    }

    [Fact]
    public void RealRecording_ReportIsProduced_AndWrittenWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy "
            + "(the live file is appended to and rotated).");

        var recording = GazeRecordingFile.Read(path!);
        var markdown = ActivationReplayReport.Build(
            recording, path!, ActivationReplayReport.Inputs.UserGeometry());

        output.WriteLine(markdown);

        // The decision-10 guard: a re-offer must never move the button away from an attempt — and no
        // offer may be left locked, because the replay accepts every fire (mutation: drop the accept).
        Check.That(markdown).Contains("| Re-offers that moved the button away | 0 (by construction, decision 10) | 0 |");
        Check.That(markdown).Contains("Locked 0");

        var outPath = Environment.GetEnvironmentVariable(ReportOutputEnvironmentVariable);
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, markdown);
    }
}
