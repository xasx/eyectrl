namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Pins the replay harness to the real <see cref="GazeEngine"/> on BOTH things it mirrors:
/// <list type="bullet">
/// <item>the pipeline's output — filtered X/Y, <see cref="GazeFrame.IsInGap"/> and timestamp on every
/// sample; and</item>
/// <item>what <c>GazeEngine.cs:604-605</c> feeds the fixation detector — the engine's onsets and ends
/// observed through <c>InteractionManager.ShowActivationButton</c>/<c>HideActivationButton</c>, which
/// fire synchronously inside <c>Push</c>. Onsets are compared by time and point; ends by count.</item>
/// </list>
/// The onset parity is the forcing function for ticket 3: when the production detector's feed or rule
/// changes, this test reds until the harness is re-pointed at ticket 3's <see cref="IFixationRule"/>
/// adapter. It is also what killed the two feed mutations (feed the raw point; feed gap frames) that
/// left every other test green.
/// </summary>
public class ReplaySliceFidelityTests
{
    /// <summary>The recording is not in the repo. Point this at the frozen snapshot COPY.</summary>
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv");

    [Fact]
    public void ReplayPipeline_MatchesTheRealGazeEngine_SampleForSample()
    {
        var recording = GazeRecordingFile.Read(FixturePath);

        var settings = new EyeCtrlSettings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), settings);
        var frames = new List<GazeFrame>();
        engine.CursorMoved += frame => frames.Add(frame);
        engine.Start();
        foreach (var sample in recording.Samples)
            provider.Push(sample);

        var replayed = ReplayPipeline.RunSynthetic(
            recording.Samples,
            new ReplayPipeline.Candidate("today", () => new OneEuroFilter()));

        Check.That(frames).HasSize(replayed.Length);
        for (int i = 0; i < replayed.Length; i++)
        {
            Check.That(frames[i].IsInGap).IsEqualTo(replayed[i].IsInGap);
            Check.That(frames[i].Position.X).IsEqualTo(replayed[i].Filtered.X);
            Check.That(frames[i].Position.Y).IsEqualTo(replayed[i].Filtered.Y);
            Check.That(frames[i].TimestampMicroseconds).IsEqualTo(replayed[i].Raw.TimestampMicroseconds);
        }
    }

    [Fact]
    public void EngineOnsets_MatchTheHarness_OnTheFixture()
    {
        AssertEngineOnsetParity(GazeRecordingFile.Read(FixturePath));
    }

    [Fact]
    public void EngineOnsets_MatchTheHarness_OnTheRecording()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy "
            + "(the live file is appended to and rotated).");

        AssertEngineOnsetParity(GazeRecordingFile.Read(path!));
    }

    /// <summary>
    /// Runs the same stream through the real engine and through the harness rule, and requires the
    /// engine's Show/HideActivationButton sequence to equal the harness's runs.
    /// </summary>
    private static void AssertEngineOnsetParity(GazeRecordingFile.Recording recording)
    {
        var settings = new EyeCtrlSettings();
        var candidate = FixationReplayGates.TodayAtRadius(settings.Dwell.FixationRadiusPixels);

        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), settings);
        var engineOnsets = new List<(long TimestampUs, PhysicalPoint Point)>();
        int engineEnds = 0;
        long currentUs = 0;
        engine.InteractionManager.ShowActivationButton += point => engineOnsets.Add((currentUs, point));
        engine.InteractionManager.HideActivationButton += () => engineEnds++;
        engine.Start();
        foreach (var sample in recording.Samples)
        {
            currentUs = sample.TimestampMicroseconds;
            provider.Push(sample);
        }

        var frames = ReplayPipeline.RunSynthetic(
            recording.Samples,
            new ReplayPipeline.Candidate(candidate.Name, candidate.CreateFilter, candidate.MaxGapMs));
        var runs = FixationReplayGates.DetectRuns(frames, candidate);

        Check.That(engineOnsets).HasSize(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            Check.That(engineOnsets[i].TimestampUs).IsEqualTo(runs[i].OnsetUs);
            Check.That(engineOnsets[i].Point.X).IsEqualTo(runs[i].Anchor.X);
            Check.That(engineOnsets[i].Point.Y).IsEqualTo(runs[i].Anchor.Y);
        }
        Check.That(engineEnds).IsEqualTo(runs.Count(r => r.EndedByLeave));
    }
}
