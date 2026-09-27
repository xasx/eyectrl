namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 3's acceptance checks, one test per row of the ticket's table. Each names the mutation that
/// turns it red; the mutations are production-code one-liners applied temporarily and reverted (see the
/// fixup reports), and the file's own helper keeps the sample clock explicit so the age arithmetic is
/// hand-checkable.
/// <para>
/// Geometry runs with the Direct filter (passthrough) wherever the centroid is asserted, so the point
/// arithmetic is the rule's own. Age tests are filter-independent (they only move in time) but keep the
/// shipped One-Euro for realism except where noted.
/// </para>
/// </summary>
public class FixationCentroidRuleTests
{
    private const long StepUs = 30_000;

    private static EyeCtrlSettings Settings(
        double radius = 75, int detectMs = 300, int activationDwellMs = 700)
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        s.Dwell.FixationRadiusPixels = radius;
        s.Dwell.FixationDetectionMs = detectMs;
        s.Dwell.ActivationButtonDwellMs = activationDwellMs;
        return s;
    }

    // Feeds `samples` samples at `stepUs` starting at fromUs (inclusive). Returns the next timestamp.
    private static long FeedValid(ScriptedGazeProvider p, double x, double y, long fromUs, int samples, long stepUs = StepUs)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            p.Push(new GazePoint(x, y, ts, true));
            ts += stepUs;
        }
        return ts;
    }

    private static long FeedInvalid(ScriptedGazeProvider p, long fromUs, int samples, long stepUs = StepUs)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            p.Push(new GazePoint(0, 0, ts, false));
            ts += stepUs;
        }
        return ts;
    }

    private sealed class Probe
    {
        public List<(long Us, PhysicalPoint Point)> Onsets { get; } = new();
        public List<FixationActivation> Activations { get; } = new();
        public List<FixationRelease> Releases { get; } = new();
        private long _currentUs;

        public Probe(GazeEngine engine)
        {
            engine.InteractionManager.ShowActivationButton += point => Onsets.Add((_currentUs, point));
            engine.FixationActivated += activation => Activations.Add(activation);
            engine.FixationReleased += release => Releases.Add(release);
        }

        public void Push(ScriptedGazeProvider provider, double x, double y, long ts, bool valid = true)
        {
            _currentUs = ts;
            provider.Push(new GazePoint(x, y, ts, valid));
        }
    }

    // ── Row 1: Drift ─────────────────────────────────────────────────────────

    [Fact]
    public void Drift_ActivatesAtDetectPlusDwell_FollowingTheRunningCentroid()
    {
        // Fixup-01 F4: the post-onset drift must exceed R from the ONSET CENTROID before activation, or
        // the ticket's named mutation ("freeze the target at the onset point") survives in CI. Here the
        // drift is 120 px over 1.2 s (100 px/s, under the 150 px/s pursuit bound at r75): onset lands at
        // sample 10 with centroid 1015, activation at sample 34 with gaze at 1102 — 87 px away, outside
        // 75. A rule that stops following (freezes at the onset point or at the first sample) ends the
        // run before activation; the running mean trails the gaze by about half the drift and holds.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long first = 1_000_000;
        long ts = first;
        for (int i = 0; i < 41; i++) // 0..1200 ms, x = 1000 → 1120 (3 px per sample)
        {
            double x = 1000 + (120.0 * i / 40.0);
            probe.Push(provider, x, 1000, ts);
            ts += StepUs;
        }

        Check.That(probe.Activations).HasSize(1);
        Check.That((double)(probe.Activations[0].TimestampUs - first)).IsCloseTo(1_020_000, 35_000);
    }

    // ── Row 2: Short blink (100 ms) ──────────────────────────────────────────

    [Fact]
    public void ShortBlink_300Plus100Plus700_FiresOnce()
    {
        // Mutation: abort on any gap → the 100 ms blink kills the run and no activation fires.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 11);      // 0..300 ms
        ts = FeedInvalid(provider, ts, samples: 3);                    // 100 ms of blink (330..390)
        Check.That(ts).IsEqualTo(420_000);                             // the first valid after the blink
        FeedValid(provider, 1000, 1000, ts, samples: 40);              // 420..1590 ms

        Check.That(probe.Onsets).HasSize(1);
        Check.That(probe.Activations).HasSize(1);
        Check.That((double)probe.Activations[0].TimestampUs).IsCloseTo(1_060_000, 65_000);
    }

    // ── Row 3: Longer blink (300 ms) ─────────────────────────────────────────

    [Fact]
    public void LongerBlink_300Plus300Plus700_FiresOnce_DespiteThe150msFilterReset()
    {
        // The GapInterpolator resets its FILTER at 150 ms, which must not abort the fixation: 3 of 33
        // real blinks fall between 150 and 500 ms. Mutation: abort the run when the interpolator's
        // long-gap reset fires → this reds.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 11);      // 0..300 ms
        ts = FeedInvalid(provider, ts, samples: 9);                    // 300 ms of blink (330..570)
        Check.That(ts).IsEqualTo(600_000);
        FeedValid(provider, 1000, 1000, ts, samples: 40);              // 600..1770 ms

        Check.That(probe.Onsets).HasSize(1);
        Check.That(probe.Activations).HasSize(1);
        Check.That((double)probe.Activations[0].TimestampUs).IsCloseTo(1_260_000, 65_000);
    }

    // ── Row 4: Lost eyes ─────────────────────────────────────────────────────

    [Fact]
    public void LostEyes_300msThen5sLostThen700ms_DoesNotFire()
    {
        // Mutation: remove the 500 ms abort → the run survives the loss and the 700 ms continuation
        // pushes the accumulated age past 1000 ms, so an activation appears.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 11);       // 0..300 ms, onset
        Check.That(probe.Onsets).HasSize(1);
        ts = FeedInvalid(provider, ts, samples: 167);                    // ~5 s lost (330..5310 ms)
        FeedValid(provider, 1000, 1000, ts, samples: 24);                // 700 ms back

        Check.That(probe.Activations).IsEmpty();
        Check.That(probe.Releases).HasSize(1);
        Check.That(probe.Releases[0].Reason).IsEqualTo(FixationEndReason.EyesLostPastGapBudget);
    }

    // ── Row 5: Gap not credited ──────────────────────────────────────────────

    [Fact]
    public void GapNotCredited_270msPlus1p2sGapPlusOneSample_DoesNotFire()
    {
        // Mutation: age = t − t_start (wall clock) → the single post-gap sample would credit 1.47 s and
        // fire onset + activation.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        FeedValid(provider, 1000, 1000, 0, samples: 10);                 // 0..270 ms, no onset yet
        probe.Push(provider, 1000, 1000, 1_470_000);                     // 1.2 s gap, one sample

        Check.That(probe.Onsets).IsEmpty();
        Check.That(probe.Activations).IsEmpty();
    }

    // ── Row 6: Gap chain (the fixup-02 instrument follow-up, pinned for the production rule) ──

    [Theory]
    [InlineData(400)]   // the fixup-02 / reviewer-B shape
    [InlineData(500)]   // the pause boundary itself: exactly 500 ms pauses, > 500 ms aborts
    public void SamePlaceGap_DoesNotActivate_AndCreditsOnlyTheCap(int gapMs)
    {
        // 0.4 s + gap + 0.4 s: capped it accrues 846 ms (< 1000), uncapped 390+gap+390 ≥ 1180 (> 1000).
        // Mutations: uncap the per-sample dt (activation appears); abort at ≥ 500 ms instead of > 500 ms
        // (the credited pin drops to the post-restart 390 because the run is torn down).
        var detector = new FixationDetector(fixationRadiusPixels: 75, fixationTimeMs: 300)
        {
            ActivationDwellMs = 700,
        };
        int activations = 0;
        detector.FixationActivated += _ => activations++;

        long ts = 0;
        for (int i = 0; i < 14; i++) // 0..390 → 390 ms credited
        {
            detector.Update(new PhysicalPoint(1000, 1000), ts);
            ts += StepUs;
        }
        ts = 390_000 + (gapMs * 1_000L);
        for (int i = 0; i < 14; i++) // +390 ms credited, with the gap's capped credit in between
        {
            detector.Update(new PhysicalPoint(1000, 1000), ts);
            ts += StepUs;
        }

        Check.That(activations).IsEqualTo(0);
        Check.That(detector.CreditedAgeUs).IsEqualTo(846_000);   // 390 + 66 capped gap credit + 390
    }

    // ── Clock teeth: the activation boundary itself ──────────────────────────

    [Theory]
    [InlineData(49, false)] // credited 980 ms, one step under the 1000 ms bar
    [InlineData(50, true)]  // credited exactly 1000 ms
    public void ActivationBoundary_JustUnderAndExactlyAtDetectPlusDwell(int lastSampleIndex, bool expected)
    {
        // Mutation: activation one frame early (subtract one step from the threshold) → the 980 ms row
        // activates. Both fixture rows (S7 990 ms / S8 1000 ms) and this production pin guard it.
        var detector = new FixationDetector(fixationRadiusPixels: 75, fixationTimeMs: 300)
        {
            ActivationDwellMs = 700,
        };
        bool activated = false;
        detector.FixationActivated += _ => activated = true;

        long ts = 0;
        for (int i = 0; i <= lastSampleIndex; i++)
        {
            detector.Update(new PhysicalPoint(1000, 1000), ts);
            ts += 20_000;
        }

        Check.That(activated).IsEqualTo(expected);
    }

    // ── Row 7: One fire ──────────────────────────────────────────────────────

    [Fact]
    public void OneFire_3p5sHold_FiresExactlyOnce()
    {
        // Mutation: remove the one-fire latch (fire on every sample past the threshold) → many fires.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        FeedValid(provider, 1000, 1000, 0, samples: 118); // 0..3510 ms

        Check.That(probe.Activations).HasSize(1);
    }

    // ── Row 8: No phantom ────────────────────────────────────────────────────

    [Fact]
    public void NoPhantom_FreshEngine_1p2sInvalid_NoFixationAt0x0()
    {
        // Mutation: revert GapInterpolator.cs:55-56 → the invalid samples are fed at (0,0) and grow a
        // phantom onset there.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedInvalid(provider, 0, samples: 41);          // 1.2 s of invalid
        FeedValid(provider, 1000, 1000, ts, samples: 11);         // then a real hold

        Check.That(probe.Onsets).HasSize(1);                      // the real hold only
        Check.That(probe.Onsets[0].Point.DistanceTo(new PhysicalPoint(0, 0))).IsStrictlyGreaterThan(100);
        Check.That(probe.Onsets[0].Point.X).IsCloseTo(1000, 1.0);
    }

    // ── Row 10: Anchor ───────────────────────────────────────────────────────

    [Fact]
    public void Anchor_OnsetPointIsTheRunningCentroid_NotTheFirstSample()
    {
        // Mutation: anchor on the first sample → the onset point becomes exactly (1000,1000).
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = 0;
        for (int i = 0; i < 12; i++) // alternates 1000/1060; the mean converges to 1030
        {
            probe.Push(provider, i % 2 == 0 ? 1000 : 1060, 1000, ts);
            ts += StepUs;
        }

        Check.That(probe.Onsets).HasSize(1);
        // Onset lands on the 11th sample (credited 300 ms), when the running mean is 1027.27 — the
        // first-sample anchor mutation reports 1000.0 exactly.
        Check.That(probe.Onsets[0].Point.X).IsCloseTo(1027.27, 2.0);
        Check.That(probe.Onsets[0].Point.DistanceTo(new PhysicalPoint(1000, 1000))).IsStrictlyGreaterThan(20);
    }

    // ── Reset edges: FreeLook entry and SuppressFixation release ─────────────

    [Fact]
    public void SuppressFixationRelease_ZeroesTheAge_AndPausedSamplesDoNotFeed()
    {
        // Mutation: drop the ResetAge request on the release edge → the pent-up 900 ms age completes
        // within three samples of the resume and an activation appears too early.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 31);  // 0..900 ms, no activation yet
        Check.That(probe.Activations).IsEmpty();

        engine.SuppressFixation = true;
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);        // suppressed: not fed
        engine.SuppressFixation = false;
        ts = FeedValid(provider, 1000, 1000, ts, samples: 3);        // resume: age restarts at the cap

        Check.That(probe.Activations).IsEmpty();                     // would fire here without the reset

        FeedValid(provider, 1000, 1000, ts, samples: 40);
        Check.That(probe.Activations).HasSize(1);                    // re-earned from zero
    }

    [Fact]
    public void FreeLookEntry_ZeroesTheAge_AndPausedSamplesDoNotFeed()
    {
        // Mutation: drop the ResetAge request on FreeLook entry → the same early activation as above.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 31);  // 0..900 ms
        Check.That(probe.Activations).IsEmpty();

        engine.InteractionManager.TogglePause();                     // Fixation → Paused
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);         // 150 ms while paused: never fed
        Check.That(probe.Activations).IsEmpty();
        engine.InteractionManager.TogglePause();                     // Paused → FreeLook (zero the age)

        ts = FeedValid(provider, 1000, 1000, ts, samples: 3);
        Check.That(probe.Activations).IsEmpty();

        FeedValid(provider, 1000, 1000, ts, samples: 40);
        Check.That(probe.Activations).HasSize(1);
    }

    [Fact]
    public void SuppressionRelease_ReArms_SoAHeldRunCanActivateAgain()
    {
        // Fixup-02 keeps the suppression-release re-arm: the release ends the old run (Suppressed — see
        // the N1 tests) and the held gaze re-earns the full clock on a fresh run, so a second activation
        // can fire while the gaze never left R. Mutation: drop the suppression-edge re-arm flag.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 40);   // onset + activation
        Check.That(probe.Activations).HasSize(1);

        engine.SuppressFixation = true;
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);         // suppressed: not fed
        engine.SuppressFixation = false;                              // re-arm, age restarts
        FeedValid(provider, 1000, 1000, ts, samples: 40);             // re-earns the full clock

        Check.That(probe.Activations).HasSize(2);
    }

    // ── Fixup-02 N1: a suppression release must end the reported run ──────────

    [Theory]
    [InlineData(200)]
    [InlineData(700)]
    public void SuppressionRelease_EndsTheReportedRun_SoAFreshHoldAtBShowsAndActivatesAtB(int suppressMs)
    {
        // Fixup-02 N1 (reviewer A's probe, confirmed on the production engine): the release edge used to
        // clear the onset latch without ending the run while the InteractionManager was still in
        // Fixation. Nothing could end it later, B's onset was rejected (OnFixationStarted only accepts
        // from FreeLook), and the state stranded with the pinned ring at A. The release now ends the
        // reported run first: Hide, FreeLook, then a fresh Show at B, and B's activation lands at B.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);

        var events = new List<string>();
        PhysicalPoint? fixationPointAtActivation = null;
        engine.InteractionManager.ShowActivationButton += p => events.Add($"Show({p.X:0})");
        engine.InteractionManager.HideActivationButton += () => events.Add("Hide");
        engine.InteractionManager.StateChanged += state => events.Add($"State({state})");
        engine.FixationActivated += _ => fixationPointAtActivation = engine.InteractionManager.FixationPoint;

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 40);   // onset at A + activation
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        engine.SuppressFixation = true;
        ts = FeedValid(provider, 1000, 1000, ts, samples: suppressMs / 30);   // suppressed: not fed
        engine.SuppressFixation = false;

        events.Clear();
        fixationPointAtActivation = null;
        var b = new PhysicalPoint(1400, 1000);
        FeedValid(provider, b.X, b.Y, ts, samples: 45);              // fresh onset at B + activation

        var hideAt = events.IndexOf("Hide");
        var freeLookAt = events.IndexOf($"State({InteractionState.FreeLook})");
        var showAtB = events.IndexOf("Show(1400)");
        Check.WithCustomMessage($"events: {string.Join(", ", events)}")
            .That(hideAt).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage($"events: {string.Join(", ", events)}")
            .That(freeLookAt).IsStrictlyGreaterThan(hideAt);
        Check.WithCustomMessage($"events: {string.Join(", ", events)}")
            .That(showAtB).IsStrictlyGreaterThan(freeLookAt);

        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
        Check.That(engine.InteractionManager.FixationPoint!.Value.DistanceTo(b)).IsStrictlyLessThan(0.5);
        Check.That(fixationPointAtActivation).IsNotNull();
        Check.WithCustomMessage("B's activation must land at B, not at the stale A")
            .That(fixationPointAtActivation!.Value.DistanceTo(b)).IsStrictlyLessThan(0.5);
    }

    [Fact]
    public void PauseResume_RaisesNoFixationRelease_NoSpuriousEnd()
    {
        // Fixup-02 item 3: Paused → FreeLook has already left Fixation, so its re-arm must stay silent.
        // An end there would be a spurious transition and would read as an abandonment in telemetry.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);

        int releases = 0;
        engine.FixationReleased += _ => releases++;

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 31);   // onset, no activation yet
        Check.That(releases).IsEqualTo(0);

        engine.InteractionManager.TogglePause();                     // Fixation → Paused (hides the button)
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);         // paused: never fed
        engine.InteractionManager.TogglePause();                     // Paused → FreeLook (silent re-arm)
        FeedValid(provider, 1000, 1000, ts, samples: 3);

        Check.That(releases).IsEqualTo(0);
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void SuppressionRelease_InRepeatMode_ReArmsTheDwell_SoNothingLegitimateIsLost()
    {
        // Fixup-02 item 2 finding: the release's end reaches OnFixationEnded, which in RepeatMode cancels
        // an armed repeat dwell. The only suppression sources that can be live WITHOUT having already
        // ended the fixation are external dwell and calibration (the keyboard toggle and the Modes menu
        // call OnFixationEnded themselves when they take suppression); both take the interaction over,
        // and the fresh onset re-arms the dwell at the user's current point, so no legitimate dwell is
        // dropped. This pins that the action still fires, once, at the fresh hold.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var im = engine.InteractionManager;
        var fired = new List<PhysicalPoint>();
        im.ActionRequested += (_, point) => fired.Add(point);

        im.EnterRepeatMode(InteractionAction.LeftClick);
        im.RepeatDwellMs = 300;

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 11);   // onset arms the dwell at A
        Check.That(fired).IsEmpty();

        engine.SuppressFixation = true;
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);         // suppressed: not fed to the detector
        engine.SuppressFixation = false;

        var b = new PhysicalPoint(1400, 1000);
        FeedValid(provider, b.X, b.Y, ts, samples: 21);               // release ends the old arm; the fresh arm at B fires

        Check.That(fired).HasSize(1);
        Check.WithCustomMessage("the dwell must be re-armed at the fresh hold, not lost")
            .That(fired[0].DistanceTo(b)).IsStrictlyLessThan(0.5);
    }

    [Fact]
    public void ResetBeforeTheFeed_NoFireOnTheFirstPostReleaseSample()
    {
        // Fixup-01 F7 / MC: the age reset must land BEFORE the feed. With 960 ms of pent-up age — within
        // one credit cap of the 1000 ms bar — a reset applied after the feed leaves the first post-release
        // sample crediting 66 ms on top of 960 and firing an activation into the just-left state, which
        // skeptic condition 2 forbids. Mutation: apply ApplyPendingFixationAgeReset after the feed.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 33);   // credited 960 ms
        Check.That(probe.Activations).IsEmpty();

        engine.SuppressFixation = true;
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);         // 150 ms suppressed: not fed
        engine.SuppressFixation = false;

        FeedValid(provider, 1000, 1000, ts, samples: 1);              // the first post-release sample

        Check.That(probe.Activations).IsEmpty();
    }

    [Fact]
    public void RedundantSuppressReleaseWrite_DoesNotZeroALiveRun()
    {
        // Fixup-01 F7 / ME: the SuppressFixation reset fires on the true → false EDGE only. A redundant
        // `= false` write (a writer re-releasing a gate it does not hold) must leave a live run's age
        // alone. Mutation: drop the `_suppressFixation &&` edge check → the redundant write zeroes the
        // run and this activation never reaches its ~1020 ms clock.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 31);   // credited 900 ms
        engine.SuppressFixation = false;                              // already false: a no-op
        FeedValid(provider, 1000, 1000, ts, samples: 4);              // credited reaches 1020 ms

        Check.That(probe.Activations).HasSize(1);
        Check.That((double)probe.Activations[0].TimestampUs).IsCloseTo(1_020_000, 35_000);
    }

    // ── F3 / R2-1: re-arm only where it helps, never into an exit-button loop ─

    [Fact]
    public void OverlayExitWithGazeHeld_DoesNotReShowUntilGazeMovesBeyondR()
    {
        // Fixup-02 R2-1: an ordinary overlay exit zeroes the age but KEEPS the latches, so a gaze that
        // stays on the exit spot does not re-onset and re-open the overlay. Round-1's rule (clear on
        // every FreeLook entry) showed the button ~300 ms later; real exits move the gaze beyond R.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 40);   // onset + activation
        Check.That(probe.Activations).HasSize(1);
        int shows = probe.Onsets.Count;

        engine.InteractionManager.OnActivationButtonDwelled();        // overlay opens, button hides
        engine.InteractionManager.OnActionMenuDismissed();            // exit to FreeLook, gaze still held

        for (int i = 0; i < 80; i++) // 2.4 s of unmoved gaze: no new Show
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Onsets.Count).IsEqualTo(shows);

        // Moving beyond R ends the run; the fresh one starts clean and its onset shows the button.
        for (int i = 0; i < 12; i++)
        {
            probe.Push(provider, 1200, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Onsets.Count).IsEqualTo(shows + 1);
    }

    [Theory]
    [InlineData(SelectionMode.None)]
    [InlineData(SelectionMode.ManualSelection)]
    public void StareAtAFixedExitAnchor_OpensExactlyOneOverlay_InEightSeconds(SelectionMode mode)
    {
        // Fixup-02 R2-1 regression (reviewer A's probe L1): the edge-bar Close sits at a FIXED anchor, so
        // an unmoved gaze can dwell it, exit, re-onset and re-activate — five overlays in 8 s on
        // f215b27d, one on the round-1 latches. Both modes are pinned because the user's is
        // ManualSelection and None follows the same path through OnActivationButtonDwelled.
        var s = Settings();
        s.SelectionMode = mode;
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);

        var anchor = new PhysicalPoint(1000, 1000);   // the activation point AND the fixed Close anchor
        int overlays = 0;
        long openedAt = -1;
        long ts = 0;

        engine.InteractionManager.ShowActionMenu += _ => { overlays++; openedAt = ts; };
        engine.InteractionManager.StartManualSelection += _ => { overlays++; openedAt = ts; };
        engine.FixationActivated += _ =>
        {
            // What the adapter does on completion: the overlay opens with its Close under the gaze.
            if (engine.InteractionManager.CurrentState == InteractionState.Fixation)
                engine.InteractionManager.OnActivationButtonDwelled();
        };

        for (int i = 0; i < 267; i++) // 8 s at 30 ms, gaze never moving
        {
            provider.Push(new GazePoint(anchor.X, anchor.Y, ts, true));
            if (openedAt >= 0 && ts - openedAt >= 600_000)
            {
                // 600 ms of dwell on the fixed Close, then the exit it performs.
                if (mode == SelectionMode.ManualSelection)
                    engine.InteractionManager.OnManualSelectionBack();
                else
                    engine.InteractionManager.OnActionMenuDismissed();
                openedAt = -1;
            }
            ts += StepUs;
        }

        Check.That(overlays).IsEqualTo(1);
    }

    [Fact]
    public void ShortPauseUnpause_ShowsTheButtonAgain_AndNoActivationFiresIntoFreeLook()
    {
        // Fixup-01 F3: a pause/unpause within 150 ms used to leave the run latched, so nothing re-showed
        // and the pent-up age fired an activation into FreeLook where no button exists. Mutation: keep
        // the latches in ResetAge → no second Show and the early FreeLook activation.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long ts = FeedValid(provider, 1000, 1000, 0, samples: 31);   // 900 ms credited, no activation
        int showsBeforePause = probe.Onsets.Count;
        Check.That(showsBeforePause).IsEqualTo(1);

        engine.InteractionManager.TogglePause();                      // Fixation → Paused
        ts = FeedValid(provider, 1000, 1000, ts, samples: 5);          // 150 ms paused: never fed
        engine.InteractionManager.TogglePause();                      // Paused → FreeLook (latches cleared)
        long resumeTs = ts;

        for (int i = 0; i < 17; i++) // ~510 ms after the resume, through the Probe for the onset time
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Activations).IsEmpty();                       // nothing fires into FreeLook
        Check.That(probe.Onsets.Count).IsEqualTo(showsBeforePause + 1); // the button is back (~300 ms)
        Check.That((double)(probe.Onsets[^1].Us - resumeTs)).IsCloseTo(300_000, 65_000);

        for (int i = 0; i < 22; i++)
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Activations).HasSize(1);                      // one activation, now in Fixation
        Check.That(probe.Activations[0].TimestampUs).IsStrictlyGreaterThan(probe.Onsets[^1].Us);
    }
}
