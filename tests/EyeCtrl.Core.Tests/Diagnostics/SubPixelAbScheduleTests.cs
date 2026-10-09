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
            // interval's 3000 ms is 100 ticks — exactly 25 px along the path.
            Check.That(motion.PositionAt(0)).IsEqualTo(start);
            Check.That(motion.PositionAt(29)).IsEqualTo(start);

            foreach ((long elapsedMs, long ticks) in new[] { (30L, 1L), (59L, 1L), (60L, 2L), (3000L, 100L) })
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
