namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Pins the replay slice to the real <see cref="GazeEngine"/>. The gate harness re-creates one line of
/// the engine's loop (<c>if (!SuppressFixation &amp;&amp; !isInGap) detector.Update(...)</c>) because the
/// engine's fixation rule is private and cannot be re-fed sample-for-sample. This test is what keeps
/// that re-creation faithful: the same stream through both must produce the same filtered point and
/// the same gap flag on every sample, so a change to the engine's feed breaks here instead of silently
/// changing what the gates measure.
/// </summary>
public class ReplaySliceFidelityTests
{
    [Fact]
    public void ReplayPipeline_MatchesTheRealGazeEngine_SampleForSample()
    {
        var recording = GazeRecordingFile.Read(
            Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", "fixation-gates-synthetic.csv"));

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
}
