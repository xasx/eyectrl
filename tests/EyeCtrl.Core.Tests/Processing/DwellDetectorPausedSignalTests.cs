namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// A1 — the per-target paused signal (<see cref="DwellDetector.TargetPauseChanged"/>) and the live
/// grace setter (<see cref="DwellDetector.SetPauseGrace"/>). The signal is display-only: it must be
/// raised when the gaze leaves a target mid-dwell under a live grace, and cleared on return, expiry,
/// reset, cooldown, removal and a switch to grace 0. Each case carries its mutant.
/// </summary>
public class DwellDetectorPausedSignalTests
{
    private const long Ms = 1_000;
    private const long Step = 30 * Ms;
    private static readonly PhysicalPoint On = new(100, 100);
    private static readonly PhysicalPoint Off = new(500, 500);

    private sealed class Recorder
    {
        public readonly List<string> Pauses = [];
        public readonly List<string> Events = [];

        public Recorder(DwellDetector detector)
        {
            detector.TargetPauseChanged += (id, paused) =>
            {
                Pauses.Add($"{id}:{(paused ? "paused" : "resumed")}");
                Events.Add($"pause {id} {paused}");
            };
            detector.TargetProgress += (id, p) => Events.Add($"progress {id} {p.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    private static DwellDetector NewDetector(int graceMs, int cooldownMs = 0)
    {
        var detector = new DwellDetector(cooldownMs, pauseGraceMs: graceMs);
        detector.AddTarget(new DwellTarget("test", On, radius: 30, dwellTimeMs: 700));
        return detector;
    }

    private static void DriveInside(DwellDetector detector, long fromUs, long toUs)
    {
        for (long t = fromUs; t <= toUs; t += Step)
            detector.Update(On, t);
    }

    [Fact]
    public void LeavingMidDwell_RaisesPaused_OnceAndReturnClearsIt()
    {
        var detector = NewDetector(500);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        Check.That(rec.Pauses).IsEmpty();

        detector.Update(Off, 210 * Ms);
        detector.Update(Off, 240 * Ms);
        detector.Update(Off, 270 * Ms);
        Check.WithCustomMessage("leaving must raise paused exactly once, not per outside sample")
            .That(rec.Pauses).ContainsExactly("test:paused");

        detector.Update(On, 300 * Ms);
        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
    }

    // Mutation (A1-p1): the leave branch never calls SetGracePaused(true) -> the paused check goes red.
    // Mutation (A1-p2): the return branch never clears -> the resumed check goes red.

    [Fact]
    public void PausedIsAnnouncedBeforeTheHeldProgress_SoAProgressDerivedCueSeesItHeld()
    {
        var detector = NewDetector(500);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        rec.Events.Clear();
        detector.Update(Off, 210 * Ms);

        Check.WithCustomMessage("the leave must announce paused first, then the frozen progress")
            .That(rec.Events.Take(2)).ContainsExactly("pause test True", "progress test 0.257");
        Check.That(detector.GetProgress("test")).IsStrictlyGreaterThan(0.0);
    }

    // Mutation (A1-p6): the paused announcement moves after the progress emission -> red.

    [Fact]
    public void AnExpiredPause_ClearsPaused_AndDropsTheProgressToZero()
    {
        var detector = NewDetector(300);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(Off, 800 * Ms);   // 620 ms after the last inside sample: past the grace

        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
        Check.That(rec.Events[^1]).IsEqualTo("progress test 0.000");
        Check.That(detector.GetProgress("test")).IsEqualTo(0.0);
    }

    // Mutation (A1-p3): the expiry pre-pass does not clear -> the second event is missing, red.

    [Fact]
    public void ReturnPastTheGrace_ClearsPaused_ExactlyOnce()
    {
        var detector = NewDetector(300);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 800 * Ms);   // back, but past the grace: restarts from zero

        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
        Check.That(detector.GetProgress("test")).IsEqualTo(0.0);
    }

    [Fact]
    public void ResetAllState_WhilePaused_ClearsPaused_AndDropsTheFrozenCue()
    {
        var detector = NewDetector(500);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.ResetAllState();

        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
        Check.WithCustomMessage("a reset must also zero the frozen progress, or the arc stays held")
            .That(rec.Events.Skip(rec.Events.IndexOf("pause test True"))).Contains("progress test 0.000");
    }

    // Mutation (A1-p4): ResetAllState does not clear the paused state / skips the 0 progress -> red.

    [Fact]
    public void ACooldown_WhilePaused_ClearsPaused_AndDropsTheFrozenCue()
    {
        var detector = new DwellDetector(cooldownMs: 400, pauseGraceMs: 500);
        detector.AddTarget(new DwellTarget("A", On, radius: 30, dwellTimeMs: 700));
        detector.AddTarget(new DwellTarget("B", new PhysicalPoint(500, 100), radius: 30, dwellTimeMs: 200));
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 120 * Ms);            // A at 120/700
        var b = new PhysicalPoint(500, 100);
        detector.Update(b, 150 * Ms);                  // A pauses, B starts
        for (long t = 180 * Ms; t <= 390 * Ms; t += Step) detector.Update(b, t);   // B activates

        // The activation starts a cooldown; the next sample clears A's held state.
        detector.Update(b, 420 * Ms);
        Check.That(rec.Pauses).Contains("A:paused");
        Check.WithCustomMessage("A cooldown reset left A's paused cue frozen")
            .That(rec.Pauses.Last(p => p.StartsWith("A:"))).IsEqualTo("A:resumed");
        Check.That(detector.GetProgress("A")).IsEqualTo(0.0);
    }

    // Mutation (A1-p5): the cooldown loop does not clear -> the last A event stays "paused", red.

    [Fact]
    public void RemovingOrClearingAPausedTarget_ClearsPaused()
    {
        var detector = NewDetector(500);
        detector.AddTarget(new DwellTarget("other", new PhysicalPoint(300, 300), radius: 20, dwellTimeMs: 700));
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.RemoveTarget("test");
        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");

        detector.AddTarget(new DwellTarget("test", On, radius: 30, dwellTimeMs: 700));
        DriveInside(detector, 240 * Ms, 420 * Ms);
        detector.Update(Off, 450 * Ms);
        detector.ClearTargets();
        Check.That(rec.Pauses.Skip(2)).ContainsExactly("test:paused", "test:resumed");
    }

    [Fact]
    public void Grace0_NeverRaisesPaused()
    {
        var detector = NewDetector(0);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 240 * Ms);
        detector.ResetAllState();

        Check.That(rec.Pauses).IsEmpty();
    }

    [Fact]
    public void ASilenceWhileStillInside_IsNotReportedAsPaused()
    {
        // The signal means "gaze left the target mid-dwell": a blink on the target keeps the same
        // arc value without a leave, so no cue change is asked for.
        var detector = NewDetector(500);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(On, 430 * Ms);   // 250 ms silence, still inside

        Check.That(rec.Pauses).IsEmpty();
    }

    // ------------------------------------------------------------------ PauseGraceExempt

    private static DwellDetector NewDetectorWithExemptTarget(int graceMs)
    {
        var detector = new DwellDetector(pauseGraceMs: graceMs);
        detector.AddTarget(new DwellTarget("test", On, radius: 30, dwellTimeMs: 700) { PauseGraceExempt = true });
        return detector;
    }

    [Fact]
    public void AnExemptTarget_UnderAGrace_ResetsOnLeave_AndRestartsFromZero()
    {
        var detector = NewDetectorWithExemptTarget(500);
        var rec = new Recorder(detector);
        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        Check.WithCustomMessage("an exempt target must report 0 on leave, not a held value")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);
        detector.Update(On, 300 * Ms);   // back well inside the 500 ms grace

        Check.WithCustomMessage("an exempt target must restart from zero on return, as at grace 0")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);
        Check.WithCustomMessage("an exempt target must never announce a pause").That(rec.Pauses).IsEmpty();

        // The fresh pass needs the whole dwell again: no activation before 700 ms of new inside time.
        DriveInside(detector, 330 * Ms, 980 * Ms);
        Check.That(activations).IsEmpty();
        detector.Update(On, 1000 * Ms);
        Check.That(activations).ContainsExactly("test");
    }

    // Mutation (A1-x1): GraceApplies ignores PauseGraceExempt -> this goes red (progress held, pause raised).

    [Fact]
    public void AnExemptTarget_UnderAGrace_RestartsOnASilenceAtTheClamp()
    {
        var detector = NewDetectorWithExemptTarget(500);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(On, 430 * Ms);   // 250 ms silence: a grace target would pause and keep 180/700

        Check.That(detector.GetProgress("test")).IsEqualTo(0.0);
    }

    // Mutation (A1-x2): the pre-pass skips the exempt branch -> the silence pauses instead, red.

    [Fact]
    public void AnExemptTarget_DoesNotChangeANeighbouringGraceTarget()
    {
        var detector = new DwellDetector(pauseGraceMs: 500);
        detector.AddTarget(new DwellTarget("held", On, radius: 30, dwellTimeMs: 700));
        detector.AddTarget(new DwellTarget("exempt", new PhysicalPoint(500, 100), radius: 30, dwellTimeMs: 700)
            { PauseGraceExempt = true });
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);

        Check.That(rec.Pauses).ContainsExactly("held:paused");
    }

    // ------------------------------------------------------------------ SetPauseGrace (live)

    [Fact]
    public void SetPauseGrace_ChangesTheGraceOfAnExistingDetector_Live()
    {
        var detector = NewDetector(0);
        Check.That(detector.PauseGraceMs).IsEqualTo(0);

        detector.SetPauseGrace(400);
        Check.That(detector.PauseGraceMs).IsEqualTo(400);

        var rec = new Recorder(detector);
        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 500 * Ms);   // 320 ms after the last inside sample: inside the 400 ms grace

        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
        Check.That(detector.GetProgress("test")).IsCloseTo(180.0 / 700.0, 1e-9);
    }

    // Mutation (A1-s1): SetPauseGrace does nothing -> PauseGraceMs stays 0 and the resume check goes red.

    [Fact]
    public void SetPauseGrace_LoweredLive_ShortensTheHold()
    {
        var detector = NewDetector(500);
        detector.SetPauseGrace(100);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 500 * Ms);   // 320 ms away: past the new 100 ms grace

        Check.That(detector.GetProgress("test")).IsEqualTo(0.0);
    }

    [Fact]
    public void SetPauseGrace_ToZero_WhilePaused_DropsTheHeldDwell_NotJustTheFutureOnes()
    {
        var detector = NewDetector(500);
        var rec = new Recorder(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.SetPauseGrace(0);

        Check.That(rec.Pauses).ContainsExactly("test:paused", "test:resumed");
        Check.WithCustomMessage("turning the grace off must also zero the held cue")
            .That(rec.Events.Skip(rec.Events.IndexOf("pause test True"))).Contains("progress test 0.000");

        detector.Update(On, 240 * Ms);   // a return now starts from zero, as today
        Check.That(detector.GetProgress("test")).IsEqualTo(0.0);
    }

    // Mutation (A1-s2): the zero branch is dropped -> the 'resumed' event never fires, red.

    [Fact]
    public void SetPauseGrace_RefusesANegativeValue()
    {
        var detector = NewDetector(100);
        ArgumentOutOfRangeException? ex = null;
        try { detector.SetPauseGrace(-1); }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("pauseGraceMs");
        Check.That(detector.PauseGraceMs).IsEqualTo(100);
    }

    // ------------------------------------------------------------------ the pairing invariant

    [Fact]
    public void OnRandomStreams_PausedAndResumedAlternate_AndNothingStaysPausedAfterAReset()
    {
        int pausedSeen = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(20_000 + seed);
            var detector = new DwellDetector(rng.Next(2) == 0 ? 0 : 300, pauseGraceMs: rng.Next(100, 700));
            detector.AddTarget(new DwellTarget("t", On, radius: 30, dwellTimeMs: 700));
            detector.AddTarget(new DwellTarget("u", new PhysicalPoint(500, 100), radius: 40, dwellTimeMs: 500));
            var state = new Dictionary<string, bool> { ["t"] = false, ["u"] = false };
            detector.TargetPauseChanged += (id, paused) =>
            {
                Check.WithCustomMessage($"seed {seed}: {id} reported {paused} twice in a row")
                    .That(state[id]).IsNotEqualTo(paused);
                state[id] = paused;
                if (paused) pausedSeen++;
            };

            long ts = 0;
            for (int step = 0; step < 400; step++)
            {
                ts += rng.Next(100) switch
                {
                    < 60 => rng.Next(5, 40) * Ms,
                    < 80 => rng.Next(150, 300) * Ms,
                    _ => rng.Next(300, 600) * Ms,
                };
                var point = rng.Next(4) switch
                {
                    0 => On,
                    1 => new PhysicalPoint(500, 100),
                    2 => new PhysicalPoint(On.X + rng.Next(-80, 81), On.Y + rng.Next(-80, 81)),
                    _ => new PhysicalPoint(900, 900),
                };
                detector.Update(point, ts);

                // A paused target always shows a held (non-zero-capable) anchor: paused implies the
                // detector is still holding it, never the other way round.
                foreach (var id in new[] { "t", "u" })
                    if (state[id])
                        Check.WithCustomMessage($"seed {seed} step {step}: {id} paused but inside")
                            .That(detector.HitTest(id, point) && detector.NearestHittingTargetId(point) == id)
                            .IsFalse();
            }

            detector.ResetAllState();
            Check.That(state["t"] || state["u"]).IsFalse();
        }

        Check.That(pausedSeen).IsStrictlyGreaterThan(100);
    }
}
