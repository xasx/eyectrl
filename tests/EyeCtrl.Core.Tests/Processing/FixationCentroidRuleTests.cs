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
        // The drift is 100 px over 1.2 s — a gentle 83 px/s, below the 150 px/s pursuit bound at r75 —
        // but MORE than one radius from where it started, which is what gives the named mutation its
        // teeth: pin any fixed anchor (the onset point, or the run's first sample, as the old rule did)
        // and the sample that passes 75 px ends the run before activation. The running mean trails the
        // gaze by about two samples, so the rule follows the drift and completes at fix + dwell.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new Probe(engine);

        long first = 1_000_000;
        long ts = first;
        for (int i = 0; i < 41; i++) // 0..1200 ms, x = 1000 → 1100
        {
            double x = 1000 + (100.0 * i / 40.0);
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
}
