namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow; // reuse ScriptedGazeProvider + RecordingInputSimulator (internal, same assembly)

public class GazeEngineFixationReconfigurationTests
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

        // Controller-style full forgiving fill → +50% band edge (=15) → effective radius 45 >= 40.
        effective.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
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

        effective.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void NonToleranceKnobFill_LeavesFixationIntact()
    {
        // PR #71 review blocker #2: SetMomentaryFill fires Changed for EVERY knob, but only FixationRadius
        // routes to the detector. A Dwell/Smoothing/Geometry fill must NOT disturb an in-progress fixation.
        // Now guaranteed structurally (the apply writes the unchanged radius back — a no-op) rather than by
        // a change-detection early return, but the user-visible contract is the same, so the test stands.
        var s = Settings(radius: 60, detectMs: 150);
        s.Adaptation.Enabled = true;
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 300, ref ts); // hold still → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        // Step a DwellTime fill (a non-tolerance knob). Changed fires, requesting an apply, which writes the
        // unchanged effective FixationRadius (still 60) back to the live detector — a no-op.
        effective.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        ts += 33_000;
        provider.Push(new GazePoint(500, 500, ts, true)); // flush the pending apply
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation); // still fixating
    }

    [Fact]
    public void WideningToleranceMidFixation_KeepsFixationAlive()
    {
        // THE FIX. Tolerance used to be applied by rebuilding the FixationDetector, and a rebuild ended the
        // in-progress fixation (returning the state machine to FreeLook and dismissing the activation button).
        // The controller steps tolerance EVERY tick (250 ms) while relaxing or recovering, and
        // FixationDetectionMs defaults to 300 ms — longer than the tick — so during a ramp no fixation could
        // ever complete: adaptation starved the very interaction it was trying to make easier. Radius is now
        // applied in place, so widening cannot disturb a live fixation (gaze inside the old radius is inside
        // the wider one).
        var s = Settings(radius: 60, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 300, ref ts); // hold still → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        s.Dwell.FixationRadiusPixels = 80; // widen mid-fixation
        ts += 33_000;
        provider.Push(new GazePoint(500, 500, ts, true)); // flushes the apply, then Updates
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void RepeatedToleranceSteps_AtControllerCadence_NeverStarveFixation()
    {
        // The ramp scenario end-to-end: ten 250 ms-spaced tolerance steps (a Conservative-posture relax from
        // fill 0 to 1 at 0.10/tick) while gaze holds still. The fixation must survive the whole ramp. Under
        // the rebuild implementation this ended the fixation on every step and, because each step arrived
        // before detectMs (300 ms) elapsed, nothing could re-fixate for the ramp's duration.
        var s = Settings(radius: 50, detectMs: 300);
        s.Adaptation.Enabled = true;
        var provider = new ScriptedGazeProvider();
        var effective = new EffectiveSettingsProvider(s.Adaptation);
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s, effectiveSettings: effective);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 400, ref ts); // hold still → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        for (int step = 1; step <= 10; step++)
        {
            effective.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other,
                forgivingFill: step * 0.10, tighteningFill: 0.0);
            DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 250, ref ts); // one controller tick of gaze
            Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
        }
    }

    [Fact]
    public void ShrinkingToleranceBelowGazeSpread_EndsFixationAsGazeLeft()
    {
        // The other half of the in-place contract (mirroring DwellDetector.UpdateTargetGeometry): shrinking
        // such that gaze now falls outside the radius ends the fixation on the next Update as an ordinary
        // gaze-left — a real transition through FixationEnded, not a synthetic teardown — so the state
        // machine returns to FreeLook and can re-fixate.
        var s = Settings(radius: 60, detectMs: 150);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 40, durationMs: 300, ref ts); // radius 60 >= 40 → fixation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        s.Dwell.FixationRadiusPixels = 20; // narrower than the 40 px spread
        ts += 33_000;
        provider.Push(new GazePoint(540, 500, ts, true)); // 40 px from the fixation center → now outside
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        DriveJitter(provider, 700, 700, spanPx: 0, durationMs: 300, ref ts); // and a fresh fixation still works
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void DetectionTimeEdit_PropagatesToLiveDetector()
    {
        // Latent bug in the rebuild implementation: a FixationDetectionMs edit requested a rebuild, but the
        // apply bailed out unless the RADIUS had changed, so the new detection time was silently dropped —
        // the settings slider did nothing. The in-place apply writes both, so shortening the requirement
        // below the already-elapsed hold fixates on the next sample.
        var s = Settings(radius: 60, detectMs: 1000);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        long ts = 0;

        DriveJitter(provider, 500, 500, spanPx: 0, durationMs: 300, ref ts); // 300 ms held, 1000 ms required
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        s.Dwell.FixationDetectionMs = 150; // now satisfied by the hold already accumulated
        ts += 33_000;
        provider.Push(new GazePoint(500, 500, ts, true));
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }
}
