namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 7, part A: the settle event and its guard, latch and threshold. One test per relevant
/// row of the ring plan's acceptance table; each names the production one-liner mutation that turns
/// it red (applied temporarily and reverted, see the ticket report).
/// <para>
/// The observed signal is <c>InteractionManager.PinRing</c> for the engine-level flows — the accepted
/// settle, which is what the ring pins on — and <see cref="FixationDetector.FixationSettled"/> for the
/// detector-level clock tests, where the engine's feed would only add pipeline noise.
/// </para>
/// <para>
/// Geometry runs with the Direct filter (passthrough) everywhere, so the point arithmetic is the
/// rule's own; age is filter-independent (it only moves in time).
/// </para>
/// </summary>
public class FixationSettleTests
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

    /// <summary>Streams valid samples at a point and records every ring pin with the sample clock.</summary>
    private sealed class PinProbe
    {
        public List<(long Us, PhysicalPoint Point)> Pins { get; } = new();
        private long _currentUs;

        public PinProbe(GazeEngine engine) =>
            engine.InteractionManager.PinRing += point => Pins.Add((_currentUs, point));

        public void Push(ScriptedGazeProvider provider, double x, double y, long ts, bool valid = true)
        {
            _currentUs = ts;
            provider.Push(new GazePoint(x, y, ts, valid));
        }

        public void Hold(ScriptedGazeProvider provider, double x, double y, long fromUs, int samples)
        {
            long ts = fromUs;
            for (int i = 0; i < samples; i++)
            {
                Push(provider, x, y, ts);
                ts += StepUs;
            }
        }
    }

    // ── No pin before settle ─────────────────────────────────────────────────

    [Fact]
    public void NoPinBeforeSettle_400msHold_NeverPins_ThenPinsOnceAtTheSettle()
    {
        // Mutation: pin on the onset (raise PinRing in InteractionManager.OnFixationStarted) → the
        // 400 ms hold pins and this reds on the first assertion.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);

        long ts = 0;
        for (int i = 0; i < 14; i++)                                 // 0..390 ms credited
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Pins).IsEmpty();

        for (int i = 0; i < 60; i++)                                 // through and past the settle
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Pins).HasSize(1);                           // exactly one settle per run
        Check.That(probe.Pins[0].Us).IsEqualTo(510_000);             // the first sample at credited >= 500

        for (int i = 0; i < 40; i++)                                 // 1.2 s more: still one
        {
            probe.Push(provider, 1000, 1000, ts);
            ts += StepUs;
        }
        Check.That(probe.Pins).HasSize(1);
    }

    // ── Settle threshold: max(onset, settle), onset first on the same sample ─

    [Fact]
    public void SettleThreshold_WithDetectionAt700_SettlesAt700OnTheOnsetSample_OnsetFirst()
    {
        // Mutation: a fixed 500 ms settle → the pin lands at credited 500 (t = 510 ms), red.
        var s = Settings(detectMs: 700);
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);

        var order = new List<string>();
        long us = 0;
        engine.InteractionManager.FixationAccepted += _ => order.Add($"accepted@{us}");
        engine.InteractionManager.PinRing += _ => order.Add($"pin@{us}");
        engine.FixationActivated += a => order.Add($"activated@{a.TimestampUs}");

        for (int i = 0; i < 50; i++)
        {
            us = i * StepUs;
            provider.Push(new GazePoint(1000, 1000, us, true));
        }

        // Onset age 700 is reached at credited 720 (t = 720 ms); settle = max(700, 500) = 700, so the
        // SAME sample settles, and the onset is raised first. Activation at 700 + 700 = 1400 credited
        // (t = 1410 ms).
        Check.That(order).ContainsExactly("accepted@720000", "pin@720000", "activated@1410000");
    }

    // ── Pin point: the running centroid at settle ────────────────────────────

    [Fact]
    public void PinPoint_IsTheRunningCentroidAtSettle_NotTheLatestOrFirstSample()
    {
        // Mutation: pin the latest sample → the pin lands at 1060, red.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);

        long ts = 0;
        for (int i = 0; i < 18; i++)   // 0..510 ms credited: settles on the 18th sample
        {
            probe.Push(provider, i % 2 == 0 ? 1000 : 1060, 1000, ts);
            ts += StepUs;
        }

        Check.That(probe.Pins).HasSize(1);
        // 18 samples, 9 of them at 1060: the running mean at settle is exactly 1030; the latest
        // sample is 1060 and the first is 1000.
        Check.That(probe.Pins[0].Point.X).IsCloseTo(1030.0, 1e-9);
        Check.That(probe.Pins[0].Point.Y).IsCloseTo(1000.0, 1e-9);
    }

    // ── Settle latched: ResetAge must not re-arm it ──────────────────────────

    [Fact]
    public void SettleLatched_ResetAge_DoesNotReArmIt()
    {
        // Mutation: re-arm the settle on ResetAge (clear _settleFired in ResetAge) → a second settle
        // fires on the next sample, red.
        var detector = new FixationDetector(fixationRadiusPixels: 75, fixationTimeMs: 300)
        {
            FixationSettleMs = 500,
        };
        var settles = new List<long>();
        detector.FixationSettled += _ => settles.Add(0);

        long ts = 0;
        for (int i = 0; i < 19; i++)   // 0..540 ms credited: settled at the 18th
        {
            detector.Update(new PhysicalPoint(1000, 1000), ts);
            ts += StepUs;
        }
        Check.That(settles).HasSize(1);

        detector.ResetAge();           // the FreeLook-entry reset: age only, latches survive

        for (int i = 0; i < 20; i++)   // a fresh 600 ms of age on the SAME run
        {
            detector.Update(new PhysicalPoint(1000, 1000), ts);
            ts += StepUs;
        }
        Check.That(settles).HasSize(1);
    }

    [Fact]
    public void SettleLatched_MenuExitAndReturn_NoSecondSettle()
    {
        // Fixup-02 R2-1's loop shape, on the settle: the gaze never moved, and the overlay exit zeroes
        // the age but keeps the latches. Note (review A F3e) what this test actually kills: clearing
        // the settle latch on FreeLook entry alone leaves it GREEN, because the re-fired settle lands
        // in FreeLook and OnFixationSettled's state guard drops it. This test pins the state guard as
        // the loop's outer defence; the latch's own re-arm is killed by
        // SettleLatched_ResetAge_DoesNotReArmIt above.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);

        probe.Hold(provider, 1000, 1000, 0, samples: 50);   // settled + activated
        Check.That(probe.Pins).HasSize(1);

        engine.InteractionManager.OnActivationButtonDwelled();   // overlay opens
        engine.InteractionManager.OnActionMenuDismissed();       // exit to FreeLook, gaze still held
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        probe.Hold(provider, 1000, 1000, 1_470_000, samples: 40); // 1.2 s unmoved
        Check.That(probe.Pins).HasSize(1);
    }

    // ── Settle guarded: only in Fixation ─────────────────────────────────────

    [Fact]
    public void SettleGuarded_AfterTheFixationEnded_IsIgnored()
    {
        // Mutation: drop the state guard in InteractionManager.OnFixationSettled → the pin fires, red.
        var manager = new InteractionManager();
        var pins = 0;
        manager.PinRing += _ => pins++;

        manager.OnFixationStarted(new PhysicalPoint(1000, 1000));
        manager.OnFixationSettled(new PhysicalPoint(1000, 1000));
        Check.That(pins).IsEqualTo(1);                  // accepted in Fixation

        manager.OnFixationEnded();                      // → FreeLook
        manager.OnFixationSettled(new PhysicalPoint(1000, 1000));
        Check.That(pins).IsEqualTo(1);                  // ignored: the run is over
    }

    // ── Release on every exit ────────────────────────────────────────────────

    [Fact]
    public void ReleaseOnEveryExit_KeyboardTogglePath_ReleasesThePin()
    {
        // The keyboard toggle sets SuppressFixation and then calls OnFixationEnded directly
        // (KeyboardCoordinator.cs:379-380): the detector stops being fed, so it raises no end. Mutation:
        // release only on the detector's end event → the pin survives the toggle and this reds.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);
        int releases = 0;
        engine.InteractionManager.ReleaseRing += () => releases++;

        probe.Hold(provider, 1000, 1000, 0, samples: 40);   // settled at ~510 ms
        Check.That(probe.Pins).HasSize(1);

        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard); // the coordinator's order
        engine.InteractionManager.OnFixationEnded();

        Check.That(releases).IsEqualTo(1);
    }

    [Fact]
    public void ReleaseOnEveryExit_PauseAndActivation_AlsoRelease()
    {
        // Mutation: release only in OnFixationEnded (not on the state transition) → the pause path
        // reds, and the menu path reds.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);
        int releases = 0;
        engine.InteractionManager.ReleaseRing += () => releases++;

        probe.Hold(provider, 1000, 1000, 0, samples: 40);           // Fixation, settled
        engine.InteractionManager.TogglePause();                    // Fixation → Paused
        Check.That(releases).IsEqualTo(1);

        probe.Hold(provider, 1000, 1000, 1_200_000, samples: 20);   // paused: never fed
        engine.InteractionManager.TogglePause();                    // Paused → FreeLook (re-arm)
        probe.Hold(provider, 1000, 1000, 2_000_000, samples: 40);   // fresh run: onset + settle
        Check.That(probe.Pins).HasSize(2);

        engine.InteractionManager.OnActivationButtonDwelled();      // Fixation → menu
        Check.That(releases).IsEqualTo(2);
    }

    // ── Pins on chrome ───────────────────────────────────────────────────────

    [Fact]
    public void SettleOverChrome_StillPins_TheChromeRuleOnlySuppressesTheOffer()
    {
        // The ring always says "you're holding" (decision 8): the pin path has no chrome input at all,
        // and the offer engine's chrome rule (part C) suppresses only the offer. This is a regression
        // guard for the day someone filters the pin by chrome: a settle in the top toolbar band would
        // then stop pinning. The no-offer half is pinned next to the engine's SetChromeZones tests.
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var probe = new PinProbe(engine);

        var inTheToolbarBand = new PhysicalPoint(1920, 30);   // 3840x2400, the top band
        probe.Hold(provider, inTheToolbarBand.X, inTheToolbarBand.Y, 0, samples: 40);

        Check.That(probe.Pins).HasSize(1);
        Check.That(probe.Pins[0].Point).IsEqualTo(inTheToolbarBand);
    }

    // ── Age is Core's valid-time ledger ──────────────────────────────────────

    [Fact]
    public void SettleAge_BlinkPausesIt_SoTheSettleShiftsByTheBlink()
    {
        // Mutation: settle on t − t_start (wall clock) → the settle lands at the wall-clock 500 ms
        // sample (t = 510 ms), red. Capped credits: 270 ms before the blink, the resume sample's
        // 300 ms gap credits only the 66 ms cap.
        var detector = new FixationDetector(fixationRadiusPixels: 75, fixationTimeMs: 300)
        {
            FixationSettleMs = 500,
        };
        var settleTimes = new List<long>();
        long currentUs = 0;
        detector.FixationSettled += _ => settleTimes.Add(currentUs);
        long fed = 0;

        void FeedValid(long ts) { currentUs = ts; detector.Update(new PhysicalPoint(1000, 1000), ts); fed = ts; }
        void FeedInvalid(long ts) { detector.Update(new PhysicalPoint(1000, 1000), ts, isValid: false); fed = ts; }

        for (long ts = 0; ts <= 270_000; ts += StepUs) FeedValid(ts);   // credited 270
        for (long ts = 300_000; ts <= 570_000; ts += StepUs) FeedInvalid(ts); // eyes lost; not fed
        for (long ts = 600_000; ts <= 1_000_000; ts += StepUs) FeedValid(ts);

        Check.That(fed).IsEqualTo(990_000);
        // Resume credits the cap (66) at t = 600 → 336; +30 a sample → the 500 ms bar is crossed at
        // t = 780 ms (credited 516). Wall-clock semantics would have settled at t = 510 ms, and
        // uncapping the per-sample dt would have credited the whole 330 ms gap at t = 600.
        Check.That(settleTimes).ContainsExactly(780_000);
    }

    [Fact]
    public void SettleAge_GapOver500ms_AbortsAndRestartsTheClock()
    {
        // Mutation: remove the 500 ms abort cap → the run survives the loss and settles one sample
        // after the resume (t = resume + 30 ms), red.
        var detector = new FixationDetector(fixationRadiusPixels: 75, fixationTimeMs: 300)
        {
            FixationSettleMs = 500,
        };
        var settleTimes = new List<long>();
        long currentUs = 0;
        detector.FixationSettled += _ => settleTimes.Add(currentUs);

        void FeedValid(long ts) { currentUs = ts; detector.Update(new PhysicalPoint(1000, 1000), ts); }

        for (long ts = 0; ts <= 390_000; ts += StepUs) FeedValid(ts);   // credited 390: not settled
        Check.That(settleTimes).IsEmpty();

        const long resumeUs = 5_400_000;
        for (long ts = resumeUs; ts <= resumeUs + 600_000; ts += StepUs) FeedValid(ts);

        // The new run starts at the resume sample: the 500 ms bar is crossed 17 samples later
        // (credited 510 at resume + 510 ms).
        Check.That(settleTimes).ContainsExactly(resumeUs + 510_000);
    }
}
