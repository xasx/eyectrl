namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// SP-4c D4 — the opt-in pause grace on <see cref="DwellDetector"/>: leaving the hit circle, or a
/// sample dropout (an invalid sample, a blink frame, a silence — all arrive here as a missed
/// update), pauses the accumulated dwell for up to the grace instead of restarting it; returning
/// within the grace resumes it, and the away time is never credited, so the full dwell still has
/// to be spent inside the circle. Each case carries its mutant.
/// </summary>
public class DwellDetectorPauseGraceTests
{
    private const long Ms = 1_000;
    private const long Step = 30 * Ms;
    private static readonly PhysicalPoint On = new(100, 100);
    private static readonly PhysicalPoint Off = new(500, 500);

    private static DwellDetector NewGraceDetector(int graceMs) => new(pauseGraceMs: graceMs);

    private static void AddTarget(DwellDetector detector, int dwellTimeMs = 700) =>
        detector.AddTarget(new DwellTarget("test", On, radius: 30, dwellTimeMs: dwellTimeMs));

    /// <summary>Delivers an inside sample every 30 ms from <paramref name="fromUs"/> to <paramref name="toUs"/>.</summary>
    private static void DriveInside(DwellDetector detector, long fromUs, long toUs)
    {
        for (long t = fromUs; t <= toUs; t += Step)
            detector.Update(On, t);
    }

    [Fact]
    public void LeaveFor250ms_KeepsTheProgress_AndResumesIt_WithoutCreditingTheAwayTime()
    {
        var detector = NewGraceDetector(300);
        AddTarget(detector);
        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        // 180 ms of the 700 ms dwell, a 250 ms exit, then back inside.
        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 430 * Ms);

        Check.WithCustomMessage("The 250 ms exit did not pause the dwell")
            .That(detector.GetProgress("test")).IsCloseTo(180.0 / 700.0, 1e-9);

        // The away time is NOT credited: the dwell completes 700 ms of INSIDE time after the
        // start, i.e. at t = 950 ms, not at t = 700 ms (where a credited away time would put it).
        DriveInside(detector, 460 * Ms, 700 * Ms);
        Check.WithCustomMessage("The away time was credited: the dwell fired 250 ms early")
            .That(activations).IsEmpty();

        DriveInside(detector, 730 * Ms, 940 * Ms);
        Check.That(activations).IsEmpty();

        detector.Update(On, 950 * Ms);
        Check.That(activations).ContainsExactly("test");
    }

    // Mutation (D4-a): the leave branch resets (EnteredTimestamp = null) instead of pausing ->
    //   the 180/700 progress check after the return goes red.

    [Fact]
    public void LeaveFor380ms_ResetsTheProgress()
    {
        var detector = NewGraceDetector(300);
        AddTarget(detector);
        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 560 * Ms);   // 380 ms after the last inside sample: past the grace

        Check.WithCustomMessage("A 380 ms exit must reset the dwell")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);

        DriveInside(detector, 590 * Ms, 1250 * Ms);
        Check.WithCustomMessage("The reset dwell fired as if the old progress had survived")
            .That(activations).IsEmpty();

        detector.Update(On, 1260 * Ms);   // 560 + 700
        Check.That(activations).ContainsExactly("test");
    }

    // Mutation (D4-b): the grace is ignored on return (always resume) -> the progress and the
    //   early-activation checks go red.

    [Fact]
    public void ASilenceOf250ms_PausesTheProgress_AndASilenceOf350ms_ResetsIt()
    {
        var paused = NewGraceDetector(300);
        AddTarget(paused);

        // 180 ms inside, then no updates at all for 250 ms (the blink/dropout shape), then inside.
        DriveInside(paused, 0, 180 * Ms);
        paused.Update(On, 430 * Ms);

        Check.WithCustomMessage("A 250 ms dropout over the 150 ms clamp was not paused")
            .That(paused.GetProgress("test")).IsCloseTo(180.0 / 700.0, 1e-9);

        var reset = NewGraceDetector(300);
        AddTarget(reset);
        DriveInside(reset, 0, 180 * Ms);
        reset.Update(On, 530 * Ms);   // 350 ms dropout

        Check.WithCustomMessage("A 350 ms dropout must restart the dwell")
            .That(reset.GetProgress("test")).IsEqualTo(0.0);
    }

    // Mutation (D4-c): the silence arm keeps the 150 ms clamp as a restart -> the 180/700 check
    //   goes red; dropping the "> grace" arm -> the reset check goes red.

    [Fact]
    public void ANearestTargetChange_WithinTheGrace_AlsoPauses()
    {
        var detector = NewGraceDetector(300);
        detector.AddTarget(new DwellTarget("A", On, radius: 30, dwellTimeMs: 700));
        detector.AddTarget(new DwellTarget("B", new PhysicalPoint(500, 100), radius: 30, dwellTimeMs: 700));

        DriveInside(detector, 0, 180 * Ms);

        // The gaze moves onto B for 250 ms, then back to A within the grace.
        detector.Update(new PhysicalPoint(500, 100), 210 * Ms);
        Check.WithCustomMessage(
                "A pause must show the FROZEN progress, not 0 (fixup 01 F2)")
            .That(detector.GetProgress("A")).IsCloseTo(180.0 / 700.0, 1e-9);

        detector.Update(On, 430 * Ms);
        Check.WithCustomMessage("A nearest-target change inside the grace did not pause the dwell")
            .That(detector.GetProgress("A")).IsCloseTo(180.0 / 700.0, 1e-9);
    }

    // Mutation (D4-d): the pause keeps accumulating while away (the anchor is not frozen) ->
    //   the progress after the return is larger than 180/700.

    [Fact]
    public void TheDefaultDetector_IsUnchanged_LeaveAndSilenceStillReset()
    {
        var detector = new DwellDetector();
        Check.WithCustomMessage("The default must stay grace 0 (every surface but the probe)")
            .That(detector.PauseGraceMs).IsEqualTo(0);

        AddTarget(detector);
        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        // The exact scenario that pauses under a grace: 250 ms out, then back.
        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 430 * Ms);

        Check.WithCustomMessage("The default detector must reset on leave")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);

        // And the dropout shape: a 250 ms silence must restart, not pause.
        DriveInside(detector, 460 * Ms, 640 * Ms);
        detector.Update(On, 890 * Ms);   // 250 ms dropout
        Check.WithCustomMessage("The default detector must reset on a dropout over the clamp")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);

        DriveInside(detector, 920 * Ms, 1580 * Ms);
        Check.WithCustomMessage("A default detector must need the full dwell on one uninterrupted run")
            .That(activations).IsEmpty();

        detector.Update(On, 1590 * Ms);   // 890 + 700
        Check.That(activations).ContainsExactly("test");
    }

    // Mutation (D4-e): the constructor defaults pauseGraceMs to 300 -> PauseGraceMs is 300, red.

    [Fact]
    public void TheGraceBoundIsInclusive_Exactly300msStillPauses()
    {
        var detector = NewGraceDetector(300);
        AddTarget(detector);

        DriveInside(detector, 0, 180 * Ms);
        detector.Update(Off, 210 * Ms);
        detector.Update(On, 480 * Ms);   // exactly 300 ms after the last inside sample

        Check.WithCustomMessage("A return at exactly the grace must still resume")
            .That(detector.GetProgress("test")).IsCloseTo(180.0 / 700.0, 1e-9);
    }

    [Fact]
    public void NegativeGrace_IsRefused()
    {
        ArgumentOutOfRangeException? ex = null;
        try { _ = new DwellDetector(pauseGraceMs: -1); }
        catch (ArgumentOutOfRangeException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("pauseGraceMs");
    }

    // ------------------------------------------------- fixup 01 F2/F3 (review 01's probes P3, P4)

    /// <summary>
    /// P4 (F2): while a dwell is paused OUTSIDE, <see cref="DwellDetector.GetProgress"/> — what C6's
    /// cue reads every frame — must report the FROZEN progress, not 0, and the leave itself must
    /// emit that frozen value. The outline must not empty during a pause the owner can still resume.
    /// </summary>
    [Fact]
    public void P4_AnOutsidePause_ShowsTheFrozenProgress_AndLeavingEmitsIt()
    {
        var detector = NewGraceDetector(300);
        AddTarget(detector);
        var emitted = new List<double>();
        detector.TargetProgress += (_, p) => emitted.Add(p);

        for (long t = 0; t <= 350 * Ms; t += 10 * Ms) detector.Update(On, t);
        double before = detector.GetProgress("test");
        Check.That(before).IsCloseTo(0.5, 1e-9);

        detector.Update(Off, 360 * Ms);
        Check.WithCustomMessage("The paused target emptied its cue: GetProgress reported 0")
            .That(detector.GetProgress("test")).IsCloseTo(before, 1e-9);
        Check.WithCustomMessage("The leave emitted 0 instead of the frozen value")
            .That(emitted[^1]).IsCloseTo(before, 1e-9);

        // The pause expires past the grace: the cue clears, and the expiry emits 0.
        detector.Update(Off, 900 * Ms);
        Check.WithCustomMessage("An expired pause must not keep showing the frozen value")
            .That(detector.GetProgress("test")).IsEqualTo(0.0);
        Check.That(emitted[^1]).IsEqualTo(0.0);
    }

    // Mutation (F2-get): GetProgress returns 0 for a non-inside graced target -> the frozen check
    //   goes red.
    // Mutation (F2-emit): the leave emits 0 -> the emission check goes red.

    /// <summary>
    /// P3 (F3): a 250 ms silence whose first post-silence sample lands OUTSIDE, then a return
    /// within the grace. The silence is subtracted once (by the pre-pass), so the return must resume
    /// at the pre-exit progress and never below 0.
    /// </summary>
    [Fact]
    public void P3_SilenceThenOutside_ThenReturn_ResumesTheFrozenProgress()
    {
        var detector = NewGraceDetector(300);
        AddTarget(detector);
        var emitted = new List<double>();
        detector.TargetProgress += (_, p) => emitted.Add(p);

        detector.Update(On, 0);
        detector.Update(On, 20 * Ms);
        detector.Update(On, 40 * Ms);                 // 40 ms in: progress 40/700
        double before = detector.GetProgress("test");
        detector.Update(Off, 290 * Ms);               // 250 ms blink, then the eye lands outside
        detector.Update(On, 310 * Ms);                // back inside, 270 ms after the last inside
        double atReturn = detector.GetProgress("test");
        detector.Update(On, 330 * Ms);
        double after = detector.GetProgress("test");

        Check.WithCustomMessage(
                $"progress went negative: before {before:F3}, at return {atReturn:F3}, next {after:F3}, "
                + $"emitted [{string.Join(", ", emitted.Select(e => e.ToString("F3")))}]")
            .That(emitted.All(e => e >= 0) && atReturn >= 0).IsTrue();
        Check.WithCustomMessage(
                $"resumed below the frozen value: before {before:F3}, at return {atReturn:F3}, next {after:F3}")
            .That(atReturn).IsCloseTo(before, 1e-9);
        Check.That(after).IsGreaterOrEqualThan(before);
    }

    // Mutation (F3): the silence arm does not advance LastInside -> the return subtracts the
    //   silence twice: atReturn is 40/700 - 250/700 = -0.300 and the non-negative check goes red.

    /// <summary>
    /// F3's property form: over random streams with a 300 ms grace, every progress the detector
    /// reports (emissions and <c>GetProgress</c> reads, inside or paused) stays in [0, 1].
    /// </summary>
    [Fact]
    public void TheGrace_KeepsEveryProgressInUnitRange_OnRandomStreams()
    {
        int checkedSamples = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var rng = new Random(10_000 + seed);
            var detector = new DwellDetector(pauseGraceMs: 300);
            detector.AddTarget(new DwellTarget("t", On, radius: 30, dwellTimeMs: 700));
            detector.AddTarget(new DwellTarget("u", new PhysicalPoint(500, 100), radius: 40, dwellTimeMs: 500));
            var emitted = new List<double>();
            detector.TargetProgress += (id, p) =>
            {
                Check.WithCustomMessage($"seed {seed}: emitted {p} for {id}")
                    .That(p).IsGreaterOrEqualThan(0.0);
                Check.WithCustomMessage($"seed {seed}: emitted {p} for {id}")
                    .That(p).IsLessOrEqualThan(1.0);
                emitted.Add(p);
            };

            long ts = 0;
            for (int step = 0; step < 400; step++)
            {
                long gap = rng.Next(100) switch
                {
                    < 60 => rng.Next(5, 40) * Ms,
                    < 80 => rng.Next(150, 300) * Ms,
                    _ => rng.Next(300, 600) * Ms,
                };
                ts += gap;
                var point = rng.Next(4) switch
                {
                    0 => On,
                    1 => new PhysicalPoint(500, 100),
                    2 => new PhysicalPoint(On.X + rng.Next(-80, 81), On.Y + rng.Next(-80, 81)),
                    _ => new PhysicalPoint(900, 900),
                };

                detector.Update(point, ts);
                checkedSamples += 2;

                foreach (var id in new[] { "t", "u" })
                {
                    double progress = detector.GetProgress(id);
                    Check.WithCustomMessage($"seed {seed} step {step}: GetProgress({id}) = {progress}")
                        .That(progress).IsGreaterOrEqualThan(0.0);
                    Check.WithCustomMessage($"seed {seed} step {step}: GetProgress({id}) = {progress}")
                        .That(progress).IsLessOrEqualThan(1.0);
                }
            }

            Check.That(emitted).Not.IsEmpty();
        }

        Check.That(checkedSamples).IsEqualTo(200 * 400 * 2);
    }
}
