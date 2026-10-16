namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// U1 — the schedule: determinism, block order, exact 5/5 counterbalancing, the static fraction
/// range, the drift step law, and requeues that keep a trial's order and parameters.
/// <para>
/// Every balance assertion runs over <see cref="SeedCount"/> seeds: review 01 measured that a single
/// seed reaches exactly 5/5 by chance with p ≈ 0.246, and that the fraction mutation survives one
/// seed with p ≈ 0.4. Repeating over 24 seeds drives both to zero.
/// </para>
/// </summary>
public class SubPixelAbScheduleTests
{
    private const int SeedCount = 24;

    private static readonly PhysicalRect Screen = new(0, 0, 2560, 1440);

    private static SubPixelAbSchedule Create(int seed) => SubPixelAbSchedule.Create(seed, Screen);

    [Fact]
    public void Create_SameSeedAndScreen_ProducesTheSamePlan()
    {
        for (int seed = 0; seed < SeedCount; seed++)
        {
            var first = Create(seed);
            var second = Create(seed);

            Check.That(second.Seed).IsEqualTo(seed);
            CheckSameTrials(first.DriftTrials, second.DriftTrials);
            CheckSameTrials(first.StaticTrials, second.StaticTrials);
            CheckSameTrials(first.LiveTrials, second.LiveTrials);
        }
    }

    [Fact]
    public void Create_RunsDriftThenStatic_ThenLive()
    {
        for (int seed = 0; seed < SeedCount; seed++)
        {
            var schedule = Create(seed);

            Check.That(schedule.Session1Trials).HasSize(20);
            CheckBlockOrder(schedule.Session1Trials, 0, SubPixelAbBlock.Drift);
            CheckBlockOrder(schedule.Session1Trials, 10, SubPixelAbBlock.Static);

            Check.That(schedule.Session2Trials).HasSize(10);
            CheckBlockOrder(schedule.Session2Trials, 0, SubPixelAbBlock.Live);
        }
    }

    [Fact]
    public void Create_EachBlockHasExactlyFiveAFirstTrials()
    {
        for (int seed = 0; seed < SeedCount; seed++)
        {
            var schedule = Create(seed);

            CheckAFirstBalance(schedule.DriftTrials);
            CheckAFirstBalance(schedule.StaticTrials);
            CheckAFirstBalance(schedule.LiveTrials);
        }
    }

    [Fact]
    public void Create_StaticTrialsParkWithFractionalPartInRange_OnBothAxes()
    {
        double minFraction = double.MaxValue;
        double maxFraction = double.MinValue;

        for (int seed = 0; seed < SeedCount; seed++)
        {
            foreach (var trial in Create(seed).StaticTrials)
            {
                var position = ((SubPixelAbStaticMotion)trial.Motion).Position;

                Check.That(position.X).IsStrictlyGreaterThan(0.0);
                Check.That(position.X).IsStrictlyLessThan(Screen.Width);
                Check.That(position.Y).IsStrictlyGreaterThan(0.0);
                Check.That(position.Y).IsStrictlyLessThan(Screen.Height);

                // The generated position is floor(anchor) + fraction, so the recovered fractional
                // part is the drawn one within double rounding at screen magnitudes.
                double fractionX = position.X - Math.Floor(position.X);
                double fractionY = position.Y - Math.Floor(position.Y);
                Check.That(fractionX).IsStrictlyGreaterThan(0.3 - 1e-9);
                Check.That(fractionX).IsStrictlyLessThan(0.7 + 1e-9);
                Check.That(fractionY).IsStrictlyGreaterThan(0.3 - 1e-9);
                Check.That(fractionY).IsStrictlyLessThan(0.7 + 1e-9);

                minFraction = Math.Min(minFraction, Math.Min(fractionX, fractionY));
                maxFraction = Math.Max(maxFraction, Math.Max(fractionX, fractionY));
            }
        }

        // The range must be sampled, not pinned: a fraction fixed at 0.5 sits inside the bounds
        // checks above and would survive them.
        Check.That(minFraction).IsStrictlyLessThan(0.4);
        Check.That(maxFraction).IsStrictlyGreaterThan(0.6);
    }

    [Fact]
    public void Create_ShufflesTheOrderAcrossSeeds()
    {
        foreach (var block in new[] { SubPixelAbBlock.Drift, SubPixelAbBlock.Static, SubPixelAbBlock.Live })
        {
            var sequences = new List<string>();
            var aFirstAt = new bool[SubPixelAbSchedule.TrialsPerBlock];
            var bFirstAt = new bool[SubPixelAbSchedule.TrialsPerBlock];

            for (int seed = 0; seed < SeedCount; seed++)
            {
                var trials = BlockOf(Create(seed), block);
                sequences.Add(new string(trials.Select(t => t.Order == SubPixelAbOrder.AFirst ? 'A' : 'B').ToArray()));

                for (int i = 0; i < trials.Count; i++)
                {
                    if (trials[i].Order == SubPixelAbOrder.AFirst) aFirstAt[i] = true;
                    else bFirstAt[i] = true;
                }
            }

            // The shuffle must actually shuffle. A block frozen to AAAAABBBBB (the no-swap mutant)
            // has exactly one sequence and never yields an early index B-first.
            Check.That(sequences.Distinct().Count()).IsStrictlyGreaterThan(19);
            for (int i = 0; i < aFirstAt.Length; i++)
            {
                Check.That(aFirstAt[i]).IsTrue();
                Check.That(bFirstAt[i]).IsTrue();
            }
        }
    }

    [Fact]
    public void Create_DifferentSeeds_ProduceDifferentPlans()
    {
        // The seed must drive the plan by design, not by luck: a constant RNG still passes the
        // balance and range assertions above.
        var zero = Create(0);
        var one = Create(1);

        bool differs = false;
        for (int i = 0; i < zero.Session1Trials.Count && !differs; i++)
            differs = !zero.Session1Trials[i].Equals(one.Session1Trials[i]);

        Check.That(differs).IsTrue();
    }

    [Fact]
    public void Create_AllTrialPositions_AndPaths_StayInTheCentralHalf()
    {
        // "Parked at a random central location": the middle half of the screen, so trials stay away
        // from the edges and the answer furniture. A schedule free to use the whole screen survives
        // the on-screen check inside the fraction test, so this pins the central region itself.
        //
        // SP-4e T1: the whole path is pinned, not only its start. A drift moves for a scripted
        // interval (0.25 px per 30 ms tick, so 41.5 px on one axis) and the live target for a live
        // one (20 px/s, so 120 px), so every start is inset by the block's maximum per-axis travel
        // and every point of the motion — not only its first frame — stays inside the region.
        double minX = Screen.Width * 0.25, maxX = Screen.Width * 0.75;
        double minY = Screen.Height * 0.25, maxY = Screen.Height * 0.75;

        double driftTravel = SubPixelAbDriftMotion.StepPixelsPerTick
            * (SubPixelAbTiming.ScriptedIntervalMs / SubPixelAbDriftMotion.TickMilliseconds);
        double liveTravel = SubPixelAbLiveMotion.PixelsPerSecond
            * (SubPixelAbTiming.LiveIntervalMs / 1000.0);

        for (int seed = 0; seed < SeedCount; seed++)
        {
            var schedule = Create(seed);

            foreach (var trial in schedule.Session1Trials.Concat(schedule.Session2Trials))
            {
                // The drift's floor-stepped law and the live target's straight path are both
                // piecewise affine, so the tick/30 ms samples below bound the whole motion.
                foreach (var point in PathPoints(trial.Motion))
                {
                    Check.That(point.X).IsStrictlyGreaterThan(minX - 1e-6);
                    Check.That(point.X).IsStrictlyLessThan(maxX + 1e-6);
                    Check.That(point.Y).IsStrictlyGreaterThan(minY - 1e-6);
                    Check.That(point.Y).IsStrictlyLessThan(maxY + 1e-6);
                }

                // The start bound itself: the draw's region is the middle half inset by the block's
                // travel. This is the assertion a start-bound that ignores the longer path fails,
                // and it is tighter than the path check above (in a moved region it fails on the
                // very first start near an edge, not only when a path happens to cross one).
                double travel = trial.Block switch
                {
                    SubPixelAbBlock.Drift => driftTravel,
                    SubPixelAbBlock.Live => liveTravel,
                    _ => 0.0,
                };
                var start = StartOf(trial.Motion);

                Check.That(start.X).IsGreaterOrEqualThan(minX + travel - 1e-6);
                Check.That(start.X).IsLessOrEqualThan(maxX - travel + 1e-6);
                Check.That(start.Y).IsGreaterOrEqualThan(minY + travel - 1e-6);
                Check.That(start.Y).IsLessOrEqualThan(maxY - travel + 1e-6);
            }
        }
    }

    [Fact]
    public void Drift_PositionFollowsTheStepLaw_InAllEightDirections()
    {
        var start = new PhysicalPoint(100.25, 200.75);

        foreach (var direction in Enum.GetValues<SubPixelAbDirection>())
        {
            var motion = new SubPixelAbDriftMotion(start, direction);
            var unit = ExpectedUnitVector(direction);

            // floor(t/30) ticks: 0–29 ms is still at the start, 30–59 ms is one tick, and the
            // interval's 5000 ms is 166 ticks — exactly 41.5 px along the path (A1: 5 s rings).
            Check.That(motion.PositionAt(0)).IsEqualTo(start);
            Check.That(motion.PositionAt(29)).IsEqualTo(start);

            foreach ((long elapsedMs, long ticks) in new[] { (30L, 1L), (59L, 1L), (60L, 2L), (5000L, 166L) })
            {
                double step = 0.25 * ticks;
                Check.That(motion.PositionAt(elapsedMs).X).IsCloseTo(start.X + step * unit.X, 1e-9);
                Check.That(motion.PositionAt(elapsedMs).Y).IsCloseTo(start.Y + step * unit.Y, 1e-9);
            }
        }
    }

    [Fact]
    public void Create_DriftDirections_CoverAllEightOverTheSeedSweep()
    {
        var seen = new HashSet<SubPixelAbDirection>();

        for (int seed = 0; seed < SeedCount; seed++)
            foreach (var trial in Create(seed).DriftTrials)
                seen.Add(((SubPixelAbDriftMotion)trial.Motion).Direction);

        Check.That(seen).HasSize(8);
    }

    [Fact]
    public void Live_TargetFollowsItsStraightPathAtTwentyPixelsPerSecond()
    {
        for (int seed = 0; seed < SeedCount; seed++)
        {
            foreach (var trial in Create(seed).LiveTrials)
            {
                var motion = (SubPixelAbLiveMotion)trial.Motion;

                Check.That(motion.TargetPositionAt(0)).IsEqualTo(motion.TargetStart);

                var afterOneSecond = motion.TargetPositionAt(1000);
                double dx = afterOneSecond.X - motion.TargetStart.X;
                double dy = afterOneSecond.Y - motion.TargetStart.Y;
                Check.That(Math.Sqrt(dx * dx + dy * dy)).IsCloseTo(20.0, 1e-9);

                // The trial carries one motion for both intervals, so "the same path in both
                // intervals" is structural rather than a re-draw that has to agree.
                Check.That(motion.TargetPositionAt(2000)).IsEqualTo(motion.TargetPositionAt(1000 + 1000));
            }
        }
    }

    [Fact]
    public void Requeue_KeepsTheTrialOrderAndParameters()
    {
        for (int seed = 0; seed < SeedCount; seed++)
        {
            var schedule = Create(seed);

            foreach (var trial in schedule.Session1Trials.Concat(schedule.Session2Trials))
            {
                var requeued = schedule.Requeue(trial);

                Check.That(requeued.Block).IsEqualTo(trial.Block);
                Check.That(requeued.IndexInBlock).IsEqualTo(trial.IndexInBlock);
                Check.That(requeued.Order).IsEqualTo(trial.Order);
                Check.That(requeued.Motion).IsEqualTo(trial.Motion);
            }
        }
    }

    [Fact]
    public void Create_RejectsANonPositiveScreen()
    {
        ArgumentOutOfRangeException? ex = null;
        try
        {
            SubPixelAbSchedule.Create(seed: 1, new PhysicalRect(0, 0, 0, 1440));
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("screenBoundsPx");
    }

    private static void CheckBlockOrder(IReadOnlyList<SubPixelAbTrial> trials, int offset, SubPixelAbBlock block)
    {
        for (int i = 0; i < SubPixelAbSchedule.TrialsPerBlock; i++)
        {
            Check.That(trials[offset + i].Block).IsEqualTo(block);
            Check.That(trials[offset + i].IndexInBlock).IsEqualTo(i);
        }
    }

    private static void CheckAFirstBalance(IReadOnlyList<SubPixelAbTrial> trials)
    {
        Check.That(trials).HasSize(SubPixelAbSchedule.TrialsPerBlock);
        Check.That(trials.Count(t => t.Order == SubPixelAbOrder.AFirst)).IsEqualTo(5);
        Check.That(trials.Count(t => t.Order == SubPixelAbOrder.BFirst)).IsEqualTo(5);
    }

    private static void CheckSameTrials(IReadOnlyList<SubPixelAbTrial> expected, IReadOnlyList<SubPixelAbTrial> actual)
    {
        Check.That(actual).HasSize(expected.Count);
        for (int i = 0; i < expected.Count; i++)
            Check.That(actual[i]).IsEqualTo(expected[i]);
    }

    private static IReadOnlyList<SubPixelAbTrial> BlockOf(SubPixelAbSchedule schedule, SubPixelAbBlock block) =>
        block switch
        {
            SubPixelAbBlock.Drift => schedule.DriftTrials,
            SubPixelAbBlock.Static => schedule.StaticTrials,
            _ => schedule.LiveTrials,
        };

    /// <summary>The start of any motion, for the central-half bounds test.</summary>
    private static PhysicalPoint StartOf(SubPixelAbMotion motion) => motion switch
    {
        SubPixelAbDriftMotion drift => drift.Start,
        SubPixelAbStaticMotion park => park.Position,
        SubPixelAbLiveMotion live => live.TargetStart,
        _ => throw new InvalidOperationException($"Unhandled motion {motion.GetType().Name}."),
    };

    /// <summary>
    /// The points whose bounds cover the whole path: every tick of a drift (the law is
    /// floor-stepped, so it is constant between ticks), every 30 ms of a live path (straight), and
    /// the single park of a static trial.
    /// </summary>
    private static IEnumerable<PhysicalPoint> PathPoints(SubPixelAbMotion motion)
    {
        switch (motion)
        {
            case SubPixelAbDriftMotion drift:
                for (long t = 0; t <= SubPixelAbTiming.ScriptedIntervalMs;
                     t += SubPixelAbDriftMotion.TickMilliseconds)
                {
                    yield return drift.PositionAt(t);
                }

                // The interval end itself, so the last partial tick is covered too.
                yield return drift.PositionAt(SubPixelAbTiming.ScriptedIntervalMs);
                break;

            case SubPixelAbStaticMotion park:
                yield return park.Position;
                break;

            case SubPixelAbLiveMotion live:
                for (long t = 0; t <= SubPixelAbTiming.LiveIntervalMs; t += 30)
                    yield return live.TargetPositionAt(t);
                break;

            default:
                throw new InvalidOperationException($"Unhandled motion {motion.GetType().Name}.");
        }
    }

    // The expected unit vectors, owned by the test rather than read from production: if the test
    // asked production for its unit vector, a mutated one would pass its own check.
    private static (double X, double Y) ExpectedUnitVector(SubPixelAbDirection direction)
    {
        double diagonal = 1.0 / Math.Sqrt(2.0);
        return direction switch
        {
            SubPixelAbDirection.East => (1.0, 0.0),
            SubPixelAbDirection.SouthEast => (diagonal, diagonal),
            SubPixelAbDirection.South => (0.0, 1.0),
            SubPixelAbDirection.SouthWest => (-diagonal, diagonal),
            SubPixelAbDirection.West => (-1.0, 0.0),
            SubPixelAbDirection.NorthWest => (-diagonal, -diagonal),
            SubPixelAbDirection.North => (0.0, -1.0),
            SubPixelAbDirection.NorthEast => (diagonal, -diagonal),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown direction."),
        };
    }
}
