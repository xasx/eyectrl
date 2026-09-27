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
/// <item>what the engine feeds the fixation detector — the engine's onsets and ends observed through
/// <c>InteractionManager.ShowActivationButton</c>/<c>HideActivationButton</c>, which fire synchronously
/// inside <c>Push</c>, plus the engine's <c>FixationActivated</c>/<c>FixationReleased</c> ledger. Onsets
/// are compared by time and point; ends by count, time and credited age (the round-3 addendum), so a
/// rule that ends a run one sample early reds.</item>
/// </list>
/// Since ticket 3 the harness candidate is the PRODUCTION rule behind
/// <see cref="ProductionFixationRule"/> and the test runs at the shipped radius (75). It is what killed
/// the two feed mutations (feed the raw point; feed gap frames) that left every other test green.
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
    /// engine's observable ledger to equal the harness's runs: onset time and point, end count, end
    /// time (last credited sample) and credited age. Ticket 3's adapter is pure forwarding, so any
    /// mismatch is a feed or pipeline difference, never a rule re-implementation.
    /// </summary>
    private static void AssertEngineOnsetParity(GazeRecordingFile.Recording recording)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 75;   // the shipped radius; the ticket's round-3 addendum
        var candidate = FixationReplayGates.CentroidAtRadius(settings.Dwell.FixationRadiusPixels);

        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), settings);
        var engineOnsets = new List<(long TimestampUs, PhysicalPoint Point)>();
        var engineActivations = new List<long>();
        var engineReleases = new List<FixationRelease>();
        int engineEnds = 0;
        long currentUs = 0;
        engine.InteractionManager.ShowActivationButton += point => engineOnsets.Add((currentUs, point));
        engine.InteractionManager.HideActivationButton += () => engineEnds++;
        engine.FixationActivated += activation => engineActivations.Add(activation.TimestampUs);
        engine.FixationReleased += release => engineReleases.Add(
            new FixationRelease(release.LastCreditedUs, release.CreditedAgeUs, release.EndedByLeave));
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
        Check.That(engineReleases).HasSize(runs.Count(r => r.EndedByLeave));
        int release = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            if (!runs[i].EndedByLeave) continue;
            Check.That(engineReleases[release].LastCreditedUs).IsEqualTo(runs[i].EndUs);
            Check.That(engineReleases[release].CreditedAgeUs).IsEqualTo(runs[i].CreditedAgeUs);
            release++;
        }

        // Fixup-01 F5 (reviewer A): activation times too. The engine reads dwell and cooldown from
        // DwellSettings while the harness pins its own constants, so a default drift would otherwise
        // leave parity green while the gates scored a rule the app no longer runs.
        var activatableRuns = runs.Where(r => r.IsActivatable).ToList();
        Check.That(engineActivations).HasSize(activatableRuns.Count);
        for (int i = 0; i < activatableRuns.Count; i++)
            Check.That(engineActivations[i]).IsEqualTo(activatableRuns[i].ActivationUs);
    }
}
