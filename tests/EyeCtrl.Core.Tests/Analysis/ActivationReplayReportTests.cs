namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Tests.Analysis;

/// <summary>
/// Ticket 7, part D: produces the replay report. The real-recording row is env-gated like every other
/// replay gate (<c>EYECTRL_GAZE_RECORDING</c> points at the frozen snapshot copy; the file is not in
/// the repo) and, when <c>EYECTRL_REPLAY_REPORT_OUT</c> names a path, writes the markdown there for
/// the report artifact. The fixture row keeps the whole analysis running in CI.
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
        // The CI-visible half: the whole walk (rule, settle, engine, corner stats, sensitivity grid)
        // runs on the committed fixture, so a break in the analysis is not hidden behind the env gate.
        var recording = GazeRecordingFile.Read(FixturePath);

        var markdown = ActivationReplayReport.Build(
            recording, FixturePath, ActivationReplayReport.Inputs.UserGeometry());

        Check.That(markdown).Contains("G3′");
        Check.That(markdown).Contains("Pop-ups / min");
        Check.That(markdown).Contains("Button on-screen share");
        Check.That(markdown).Contains("Re-offers that moved the button away from an attempt");
        Check.That(markdown).Contains("Sensitivity — settle time");
        Check.That(markdown).Contains("Sensitivity — hit radius");
        Check.That(markdown).Contains("Ring still share");
        Check.That(markdown).Contains("G3′ is a floor");
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

        // The decision-10 guard: a re-offer must never move the button away from an attempt.
        Check.That(markdown).Contains("| Re-offers that moved the button away from an attempt | 0 |");

        var outPath = Environment.GetEnvironmentVariable(ReportOutputEnvironmentVariable);
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, markdown);
    }
}
