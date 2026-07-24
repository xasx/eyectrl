namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow; // reuse ScriptedGazeProvider + RecordingInputSimulator (internal, same assembly)

public class GazeEngineFixationRebuildTests
{
    private static EyeCtrlSettings Settings(double radius, int detectMs)
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct; // passthrough: filtered == raw
        s.Dwell.FixationRadiusPixels = radius;
        s.Dwell.FixationDetectionMs = detectMs;
        return s;
    }

    // Alternates gaze between (cx,cy) and (cx+spanPx, cy) every 33 ms for durationMs. The maximum
    // distance from the first point is exactly spanPx, so the cluster is ONE fixation iff the detector
    // radius >= spanPx; with radius < spanPx the center keeps resetting and never fixates. spanPx: 0
    // holds gaze still — a guaranteed fixation for any positive radius.
    private static void DriveJitter(ScriptedGazeProvider p, double cx, double cy, double spanPx, int durationMs, ref long ts)
    {
        bool far = false;
        for (int elapsed = 0; elapsed < durationMs; elapsed += 33)
        {
            ts += 33_000;
            p.Push(new GazePoint(far ? cx + spanPx : cx, cy, ts, true));
            far = !far;
        }
    }

    [Fact]
    public void ManualToleranceWidening_PropagatesToLiveDetector()
    {
        // Latent-bug fix: GazeEngine previously subscribed only to CursorSettings, so this edit was ignored.
        var s = Settings(radius: 20, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // radius 20 < 40 → no fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        s.Dwell.FixationRadiusPixels = 120; // widen at runtime → rebuild
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // radius 120 >= 40 → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void AdaptationDelta_LoosensToleranceLive_WhenEnabled()
    {
        var s = Settings(radius: 30, detectMs: 150);
        s.Adaptation.Enabled = true;
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // baseline 30 < 40 → no fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        // Controller-style forgiving delta: +100 clamps to +50% (=15) → effective radius 45 >= 40.
        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void AdaptationDelta_HasNoEffect_WhenDisabled()
    {
        // Exit invariant: disabled ⇒ effective == baseline, so the delta is inert.
        var s = Settings(radius: 30, detectMs: 150); // Adaptation.Enabled defaults false
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RebuildMidFixation_EndsCleanly_SoNextFixationRetriggers()
    {
        // Review round-1 (reviewer F2): a rebuild while CurrentState == Fixation must NOT strand the state
        // machine — unwiring the old detector fires no FixationEnded, and a fresh FixationStarted no-ops
        // unless in FreeLook, so RebuildFixationDetector ends the in-progress fixation cleanly.
        var s = Settings(radius: 60, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 300, ref ts); // hold still → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        s.Dwell.FixationRadiusPixels = 80; // rebuild mid-fixation → clean end → FreeLook
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        DriveJitter(provider, 700, 700, spanPx: 0, durationMs: 300, ref ts); // fresh fixation re-triggers
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }
}
