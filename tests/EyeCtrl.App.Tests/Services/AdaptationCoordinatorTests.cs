namespace EyeCtrl.App.Tests.Services;

using System;
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

    // Minimal IGazeProvider fake: only IsConnected is exercised. GazePointReceived uses explicit no-op
    // accessors so there is no backing field (avoids CS0067 "event never used"). The Capabilities /
    // TrackStatusReceived / TryGetLatestStatus members are default-implemented on the interface.
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public bool IsConnected { get; set; } = true;
        event Action<GazePoint>? IGazeProvider.GazePointReceived { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    // Builds the coordinator over REAL Core pieces (headless — no WPF): a real sink/monitor/provider/
    // controller, a fake gaze provider, and a real interaction manager. Returns the handles tests poke.
    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, FakeGazeProvider Gaze, InteractionManager Im) Make(bool enabled)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        var gaze = new FakeGazeProvider();
        var im = new InteractionManager();
        var coord = new AdaptationCoordinator(monitor, controller, settings.Adaptation, gaze, im);
        return (coord, provider, sink, settings.Adaptation, gaze, im);
    }

    private static DwellOutcome Abandon(long ts, double progress) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
        new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30, null);

    [Fact]
    public void Disabled_SkipsAllWork()
    {
        var (c, _, _, _, _, _) = Make(enabled: false);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disabled);
    }

    [Fact]
    public void FirstEnabledTick_ResetsOnEnable()
    {
        var (c, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable);
    }

    [Fact]
    public void AwaitingGaze_WhenNoSampleYet()
    {
        var (c, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnEnable); // tick 1: fresh session
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.AwaitingGaze); // tick 2: now == Unseeded (no gaze)
    }

    [Fact]
    public void DisableAfterActive_ClearsOnceThenReportsDisabled()
    {
        var (c, _, _, adaptation, _, _) = Make(enabled: true);
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
        var (c, _, _, _, gaze, _) = Make(enabled: true);
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
        var (c, _, _, _, _, im) = Make(enabled: true);
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
        var (c, provider, sink, _, _, _) = Make(enabled: true);
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
        var (c, provider, sink, _, _, _) = Make(enabled: true);
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
        var (c, _, sink, _, _, im) = Make(enabled: true);
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
        var (c, _, _, adaptation, _, _) = Make(enabled: true);
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
}
