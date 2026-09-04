namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationCoordinatorTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // Builds the coordinator over REAL Core pieces (headless — no WPF): a real sink/monitor/provider/
    // controller, a fake gaze provider, and a real interaction manager. Returns the handles tests poke.
    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, TestFakes.FakeGazeProvider Gaze, InteractionManager Im,
                    AdaptationLearner Learner, string StatePath) Make(bool enabled)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        string statePath = Path.Combine(Path.GetTempPath(), "eyectrl_learner_coord_test_" + Guid.NewGuid() + ".json");
        var learner = new AdaptationLearner(provider, settings.Adaptation, stateFilePath: statePath);
        var gaze = new TestFakes.FakeGazeProvider();
        var im = new InteractionManager();
        var coord = new AdaptationCoordinator(monitor, controller, learner, settings.Adaptation, gaze, im);
        return (coord, provider, sink, settings.Adaptation, gaze, im, learner, statePath);
    }

    private static DwellOutcome Abandon(long ts, double progress) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
        new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30, null);

    [Fact]
    public void Disabled_SkipsAllWork()
    {
        var (c, _, _, _, _, _, _, _) = Make(enabled: false);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disabled);
    }

    [Fact]
    public void FirstEnabledTick_ResetsOnEnable()
    {
        var (c, _, _, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable);
    }

    [Fact]
    public void AwaitingGaze_WhenNoSampleYet()
    {
        var (c, _, _, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable); // tick 1: fresh session
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.AwaitingGaze); // tick 2: now == Unseeded (no gaze)
    }

    [Fact]
    public void DisableAfterActive_ClearsOnceThenReportsDisabled()
    {
        var (c, _, _, adaptation, _, _, _, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnEnable
        c.Tick(); // Assessed
        adaptation.Enabled = false;
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ClearedOnDisable);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disabled);
    }

    [Fact]
    public void ProviderDisconnect_ResetsThenReportsDisconnected()
    {
        var (c, _, _, _, gaze, _, _, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnEnable
        c.Tick(); // Assessed
        gaze.IsConnected = false;
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnDisconnect); // the ③c F3 carry-forward
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disconnected);
    }

    [Fact]
    public void ResumeFromPause_Resets()
    {
        var (c, _, _, _, _, im, _, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnEnable
        c.Tick(); // Assessed
        im.TogglePause(); // FreeLook → Paused
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Paused);        // into-pause: skip (no reset)
        im.TogglePause(); // Paused → FreeLook (resume)
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnResume); // resume edge: reset + skip
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Assessed);      // back to active
    }

    [Fact]
    public void ActiveTick_AssessesAndAdaptsTolerance()
    {
        var (c, provider, sink, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable); // now == Unseeded → sessionStart = long.MinValue (excludes nothing)

        // Gate SustainFailure: 4 high-progress abandons in one context (abandonment score 0.8, ≥ 4 attempts).
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8));

        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true));
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Assessed);     // relaxSince anchored at 40 s
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true));
        c.Tick();                                                            // 3 s sustain met → tolerance relaxes one step

        // Tolerance loosened above baseline (the one observably-routed knob).
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
    }

    [Fact]
    public void Reset_ClearsMomentaryAdaptation()
    {
        var (c, provider, sink, _, _, _, _, _) = Make(enabled: true);
        c.Tick(); // ResetOnEnable
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8));
        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); c.Tick();
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); c.Tick();
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        c.Reset(); // the always-reachable escape (§8.6)
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void LastAssessment_ClearsWhenNotAssessing()
    {
        // The cue reflects only LIVE struggle: LastAssessment is None whenever the last tick did not assess.
        var (c, _, sink, _, _, im, _, _) = Make(enabled: true);
        c.Tick(); // ResetOnEnable (now == Unseeded → sessionStart = long.MinValue, excludes nothing)
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8));
        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); c.Tick();
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); c.Tick(); // Assessed with gated SustainFailure
        Check.That(c.LastAssessment.AggregateLevel > 0.0).IsTrue();     // live struggle present

        im.TogglePause(); // FreeLook → Paused
        c.Tick();         // Paused → LastAssessment cleared
        Check.That(c.LastAssessment.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void ReEnableAfterDisable_FiresResetOnEnable()
    {
        // The enable→disable→enable cycle: re-enable must take the ResetOnEnable branch (the
        // reset-edge ternary gives !_wasEnabled priority), ignoring any stale _wasConnected/_wasPaused.
        var (c, _, _, adaptation, _, _, _, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnEnable (first enable)
        c.Tick(); // Assessed
        adaptation.Enabled = false;
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ClearedOnDisable); // _wasEnabled -> false
        adaptation.Enabled = true;
        // Re-enable: _wasEnabled is false → ResetOnEnable wins the ternary, regardless of stale flags.
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Assessed); // back to active
    }

    // Sample cadence note (review round 1, finding 9 — the first draft's inline reasoning was wrong):
    // the learner samples on the FIRST assess tick (no seed-only step) and then every ≥ 30 s. So the
    // sample points here are t=40 (observes fill 0 — the controller has only anchored relaxSince and
    // pushed nothing yet), t=73 (observes fill 0.2 → learned 0.01, a knife-edge ε margin), and t=103
    // (observes fill 0.3 → learned ≈ 0.0245, a solid push). The 103 s tick exists to get the assertion
    // off the knife edge; do not remove it. The abandon stream runs through t=90 (nine reports): the
    // 60 s evidence max-age means a stream ending at 70 would leave only 3 in-window attempts at
    // t=103, failing the ≈4-attempt gate and *recovering* the fill instead of stepping it to 0.3.
    private static void DriveToLearnedOffset(AdaptationCoordinator c, BufferingTelemetrySink sink)
    {
        c.Tick(); // ResetOnEnable
        for (int i = 0; i < 9; i++)
            sink.Report(Abandon((i + 1) * 10 * Sec, progress: 0.8)); // gate SustainFailure through t=90 s
        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); c.Tick();  // anchor relaxSince; learner sample 1 (observes 0)
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); c.Tick();  // 3 s sustain → fill 0.1 (within cadence — no sample)
        c.OnGazeSample(new GazePoint(0, 0, 73 * Sec, true)); c.Tick();  // fill 0.2; learner sample 2 (0.01 — marginal)
        c.OnGazeSample(new GazePoint(0, 0, 103 * Sec, true)); c.Tick(); // fill 0.3; learner sample 3 → solid push (≥ ε)
    }

    [Fact]
    public void ActiveTicks_WithSustainedStruggle_AccumulateLearnedOffset()
    {
        var (c, provider, sink, _, _, _, _, _) = Make(enabled: true);
        DriveToLearnedOffset(c, sink);
        Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
    }

    [Fact]
    public void ManualReset_ClearsLearned_AndPersistsZeroState()
    {
        var (c, provider, sink, _, _, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();

            c.Reset(); // the manual §8.6 escape: momentary AND learned

            Check.That(provider.EnumerateLearnedFills()).IsEmpty();
            Check.That(provider.TryGetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, out _, out _)).IsFalse(); // momentary store cleared
            Check.That(AdaptationStateFile.Load(statePath).Learned.Values).ContainsOnlyElementsThatMatch(v => v == 0.0);
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void SessionEdgeResets_PreserveLearnedOffsets()
    {
        var (c, provider, sink, _, gaze, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            gaze.IsConnected = false;
            Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnDisconnect);
            // §13.1: learned persists across session boundaries — only momentary evidence resets.
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
            Check.That(provider.TryGetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, out _, out _)).IsFalse();
        }
        finally { File.Delete(statePath); }
    }

    [Fact]
    public void DisableEdge_PreservesLearnedOffsets()
    {
        var (c, provider, sink, adaptation, _, _, _, statePath) = Make(enabled: true);
        try
        {
            DriveToLearnedOffset(c, sink);
            adaptation.Enabled = false;
            Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ClearedOnDisable);
            // Learned survives disable inertly: Effective gates on Enabled first (§8.3/§9.1).
            Check.That(provider.EnumerateLearnedFills()).Not.IsEmpty();
            Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(50.0);
        }
        finally { File.Delete(statePath); }
    }
}
