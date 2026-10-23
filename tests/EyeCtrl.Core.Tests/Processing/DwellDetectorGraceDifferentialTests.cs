namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// SP-4c D4 — the two guarantees the grace must not break, ported from review 01's probes (P1 and
/// P2; P5 was a characterisation that fails by design and is deliberately not adopted).
///
/// <para>
/// P1 pins the promise every non-probe caller relies on: a detector with the default grace 0 is
/// behaviour-identical to the base (<c>a872f36a</c>) detector, over 300 random streams
/// (240,000 steps), comparing every progress emission, activation, telemetry outcome and
/// <c>GetProgress</c> read. <see cref="Rv4cOldDwellDetector"/> is a verbatim copy of that base
/// detector, kept in the test project as the reference.
/// </para>
///
/// <para>
/// P2 pins the grace 300 side: an activation needs the gaze inside that target on that sample, and
/// the credited dwell never exceeds the inside-to-inside sample time, so outside time is never
/// credited even though a pause keeps the accumulated dwell.
/// </para>
/// </summary>
public class DwellDetectorGraceDifferentialTests
{
    private const long Ms = 1_000;

    private sealed class Sink : IStruggleTelemetrySink
    {
        public readonly List<string> Log = [];
        public void Report(in DwellOutcome outcome) => Log.Add(outcome.ToString());
    }

    private static List<DwellTarget> NewTargets(int[] dwell) =>
    [
        new DwellTarget("a", new PhysicalPoint(100, 100), 40, dwell[0]),
        new DwellTarget("b", new PhysicalPoint(160, 100), 40, dwell[1]),
        new DwellTarget("c", new PhysicalPoint(400, 300), 60, dwell[2]),
        new DwellTarget("r", new PhysicalRect(600, 600, 100, 80), dwell[3]),
    ];

    private static readonly PhysicalPoint[] Anchors =
        [new(100, 100), new(160, 100), new(130, 100), new(400, 300), new(650, 640), new(900, 900)];

    /// <summary>P1: grace 0 is behaviour-identical to the base (a872f36a) detector on random streams.</summary>
    [Fact]
    public void P1_GraceZero_IsIdenticalToTheBaseDetector_OnRandomStreams()
    {
        int checkedSteps = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            int cooldown = rng.Next(3) switch { 0 => 0, 1 => 150, _ => 400 };
            int[] dwell = [rng.Next(200, 900), rng.Next(200, 900), rng.Next(200, 900), rng.Next(200, 900)];
            bool withSink = rng.Next(2) == 0;
            var sinkOld = new Sink();
            var sinkNew = new Sink();
            var oldD = new Rv4cOldDwellDetector(cooldown, DwellContext.Other, withSink ? sinkOld : null);
            // The production configuration of the overlay family: the neighbour release is on. At
            // grace 0 it must be inert (A1 fixup-02 L1).
            var newD = new DwellDetector(cooldown, DwellContext.Other, withSink ? sinkNew : null)
                { OtherTargetReleasesHold = true };
            var logOld = new List<string>();
            var logNew = new List<string>();
            oldD.TargetProgress += (id, p) => logOld.Add($"P {id} {p:R}");
            newD.TargetProgress += (id, p) => logNew.Add($"P {id} {p:R}");
            // A1: the paused signal and the live setter are display/config additions; at grace 0
            // the signal must never fire and the setter must be a pure no-op.
            var pauseEvents = new List<string>();
            newD.TargetPauseChanged += (id, paused) => pauseEvents.Add($"{id} {paused}");
            oldD.TargetActivated += id => logOld.Add($"A {id}");
            newD.TargetActivated += id => logNew.Add($"A {id}");
            foreach (var t in NewTargets(dwell)) oldD.AddTarget(t);
            foreach (var t in NewTargets(dwell)) newD.AddTarget(t);

            long ts = rng.Next(0, 1000) * Ms;
            for (int step = 0; step < 800; step++)
            {
                int gapKind = rng.Next(100);
                long gap = gapKind < 3 ? 0
                    : gapKind < 70 ? rng.Next(5, 40) * Ms
                    : gapKind < 85 ? rng.Next(100, 200) * Ms
                    : rng.Next(200, 450) * Ms;
                ts += gap;

                var anchor = Anchors[rng.Next(Anchors.Length)];
                var p = new PhysicalPoint(anchor.X + rng.Next(-70, 71), anchor.Y + rng.Next(-50, 51));

                int op = rng.Next(200);
                if (op == 0) { oldD.ResetAllState(); newD.ResetAllState(); }
                else if (op == 1) { oldD.ResetCooldown(); newD.ResetCooldown(); }
                else if (op == 2)
                {
                    var c = new PhysicalPoint(rng.Next(50, 500), rng.Next(50, 500));
                    oldD.UpdateTargetCenter("c", c); newD.UpdateTargetCenter("c", c);
                }
                else if (op == 3)
                {
                    double m = rng.Next(-10, 30);
                    oldD.SetEffectiveGeometry(e => e + m); newD.SetEffectiveGeometry(e => e + m);
                }
                else if (op == 4)
                {
                    oldD.RemoveTarget("b"); newD.RemoveTarget("b");
                    oldD.AddTarget(new DwellTarget("b", new PhysicalPoint(160, 100), 40, dwell[1]));
                    newD.AddTarget(new DwellTarget("b", new PhysicalPoint(160, 100), 40, dwell[1]));
                }
                else if (op == 5)
                {
                    int ms = rng.Next(0, 500);
                    oldD.SetEffectiveDwellTime(b => b + ms); newD.SetEffectiveDwellTime(b => b + ms);
                }
                else if (op == 6)
                {
                    newD.SetPauseGrace(0);   // restating grace 0 must change nothing
                }

                if (rng.Next(10) == 0)
                {
                    string excluded = rng.Next(2) == 0 ? "a" : "b";
                    oldD.Update(p, ts, t => t.Id != excluded);
                    newD.Update(p, ts, t => t.Id != excluded);
                }
                else
                {
                    oldD.Update(p, ts);
                    newD.Update(p, ts);
                }

                foreach (var id in new[] { "a", "b", "c", "r" })
                {
                    logOld.Add($"G {id} {oldD.GetProgress(id):R}");
                    logNew.Add($"G {id} {newD.GetProgress(id):R}");
                }

                checkedSteps++;
            }

            Check.WithCustomMessage($"seed {seed}: event logs differ")
                .That(string.Join("\n", logNew)).IsEqualTo(string.Join("\n", logOld));
            Check.WithCustomMessage($"seed {seed}: a grace-0 detector raised TargetPauseChanged")
                .That(pauseEvents).IsEmpty();
            Check.WithCustomMessage($"seed {seed}: telemetry differs")
                .That(string.Join("\n", sinkNew.Log)).IsEqualTo(string.Join("\n", sinkOld.Log));
        }

        Check.That(checkedSteps).IsEqualTo(300 * 800);
    }

    /// <summary>
    /// A1: restating grace 0 on a live detector mid-cooldown must change nothing — the setter may not
    /// clear the activation cooldown or touch any other state a grace-0 detector owns. The random
    /// streams above hit that window too rarely to pin it, so it is driven deterministically.
    /// </summary>
    [Fact]
    public void P1b_SetPauseGraceZero_DuringACooldown_ChangesNothing()
    {
        var oldD = new Rv4cOldDwellDetector(400, DwellContext.Other, null);
        var newD = new DwellDetector(400, DwellContext.Other, null) { OtherTargetReleasesHold = true };
        long now = 0;
        var logOld = new List<string>();
        var logNew = new List<string>();
        oldD.TargetProgress += (id, p) => logOld.Add($"P {id} {p:R} @{now}");
        newD.TargetProgress += (id, p) => logNew.Add($"P {id} {p:R} @{now}");
        oldD.TargetActivated += id => logOld.Add($"A {id} @{now}");
        newD.TargetActivated += id => logNew.Add($"A {id} @{now}");
        oldD.AddTarget(new DwellTarget("a", new PhysicalPoint(100, 100), 40, 200));
        newD.AddTarget(new DwellTarget("a", new PhysicalPoint(100, 100), 40, 200));

        var on = new PhysicalPoint(100, 100);
        for (long t = 0; t <= 300; t += 30)
        {
            now = t;
            oldD.Update(on, t * Ms);
            newD.Update(on, t * Ms);
        }

        newD.SetPauseGrace(0);   // inside the 400 ms cooldown that the activation started

        for (long t = 330; t <= 900; t += 30)
        {
            now = t;
            oldD.Update(on, t * Ms);
            newD.Update(on, t * Ms);
        }

        Check.That(logOld).Not.IsEmpty();
        Check.That(string.Join("\n", logNew)).IsEqualTo(string.Join("\n", logOld));
    }

    // Mutation (A1-d1): SetPauseGrace clears the activation cooldown -> the dwell re-accumulates inside
    //   the cooldown and the two logs diverge.

    // Mutation (O8): the grace pre-pass is taken at grace 0 -> the differential's GetProgress log
    //   diverges, and the existing DwellDetectorTests go red too.

    /// <summary>
    /// P2: grace 300 — an activation needs the gaze inside the activated target on that sample, and
    /// the credited dwell never exceeds the inside-to-inside sample time (each gap below the 150 ms
    /// clamp) since the target's previous activation. Random multi-target streams.
    /// </summary>
    [Fact]
    public void P2_Grace300_NeverCreditsOutsideTime_AndNeverActivatesWhileOutside()
    {
        int activationsSeen = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(1000 + seed);
            int[] dwell = [rng.Next(300, 900), rng.Next(300, 900), rng.Next(300, 900), rng.Next(300, 900)];
            var d = new DwellDetector(rng.Next(2) == 0 ? 0 : 300, pauseGraceMs: 300);
            var targets = NewTargets(dwell);
            foreach (var t in targets) d.AddTarget(t);

            var budget = new Dictionary<string, long> { ["a"] = 0, ["b"] = 0, ["c"] = 0, ["r"] = 0 };
            string? prevNearest = null;
            int anchorIdx = 0;
            long ts = 0;
            string? activated = null;
            d.TargetActivated += id => activated = id;

            for (int step = 0; step < 1500; step++)
            {
                int gapKind = rng.Next(100);
                long gap = gapKind < 75 ? rng.Next(5, 40) * Ms
                    : gapKind < 90 ? rng.Next(100, 320) * Ms
                    : rng.Next(320, 500) * Ms;
                ts += gap;
                if (rng.Next(100) < 8) anchorIdx = rng.Next(Anchors.Length);
                var anchor = Anchors[anchorIdx];
                var p = new PhysicalPoint(anchor.X + rng.Next(-45, 46), anchor.Y + rng.Next(-35, 36));

                string? nearest = d.NearestHittingTargetId(p);
                if (nearest is not null && nearest == prevNearest && gap < 150 * Ms)
                    budget[nearest] += gap;

                activated = null;
                d.Update(p, ts);
                if (activated is { } id)
                {
                    activationsSeen++;
                    var target = targets.Single(t => t.Id == id);
                    Check.WithCustomMessage($"seed {seed} step {step}: {id} activated with the gaze outside")
                        .That(nearest).IsEqualTo(id);
                    Check.WithCustomMessage(
                            $"seed {seed} step {step}: {id} activated with only {budget[id] / Ms} ms of "
                            + $"inside time against a {target.DwellTimeMicroseconds / Ms} ms dwell")
                        .That(budget[id]).IsGreaterOrEqualThan(target.DwellTimeMicroseconds);
                    budget[id] = 0;
                }

                prevNearest = nearest;
            }
        }

        Check.That(activationsSeen).IsStrictlyGreaterThan(50);
    }

    // Mutation (O7): the resume shift credits the away time -> activations fire with too little
    //   inside time and P2 goes red.
}
