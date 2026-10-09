namespace EyeCtrl.Core.Diagnostics;

using EyeCtrl.Core.Models;

/// <summary>The three blocks of the sub-pixel visibility test, in the order they run.</summary>
public enum SubPixelAbBlock
{
    /// <summary>Scripted drift; 10 trials; decides session 1.</summary>
    Drift,

    /// <summary>Fractional still parks; 10 trials; supporting evidence only, never convicts.</summary>
    Static,

    /// <summary>Live-gaze trials on a slow target path; session 2, run only after a session-1 conviction.</summary>
    Live,
}

/// <summary>Which condition is shown in the first interval of a trial; the other one follows.</summary>
public enum SubPixelAbOrder
{
    /// <summary>Whole-pixel (production-like) condition in interval 1, sub-pixel in interval 2.</summary>
    AFirst,

    /// <summary>Sub-pixel condition in interval 1, whole-pixel in interval 2.</summary>
    BFirst,
}

/// <summary>
/// The eight compass directions a drift trial can move in. Screen coordinates: Y grows downward, so
/// <see cref="South"/> moves down the screen. The names are labels; only the step law is normative.
/// </summary>
public enum SubPixelAbDirection
{
    /// <summary>Toward +X.</summary>
    East,
    /// <summary>Toward +X and +Y.</summary>
    SouthEast,
    /// <summary>Toward +Y.</summary>
    South,
    /// <summary>Toward −X and +Y.</summary>
    SouthWest,
    /// <summary>Toward −X.</summary>
    West,
    /// <summary>Toward −X and −Y.</summary>
    NorthWest,
    /// <summary>Toward −Y.</summary>
    North,
    /// <summary>Toward +X and −Y.</summary>
    NorthEast,
}

/// <summary>
/// One scheduled trial: the block it belongs to, its position in that block, which condition runs
/// first, and the motion both intervals run. Immutable and pure — the same trial is replayed when a
/// voided trial is repeated.
/// </summary>
/// <param name="Block">The block this trial belongs to.</param>
/// <param name="IndexInBlock">0-based position within its block, in run order.</param>
/// <param name="Order">Which condition is shown first; the other follows in interval 2.</param>
/// <param name="Motion">The motion parameters, identical in both intervals of the trial.</param>
public sealed record SubPixelAbTrial(
    SubPixelAbBlock Block,
    int IndexInBlock,
    SubPixelAbOrder Order,
    SubPixelAbMotion Motion);

/// <summary>The motion parameters of one trial, with the law that turns them into a position.</summary>
public abstract record SubPixelAbMotion;

/// <summary>
/// Scripted drift: the ring advances 0.25 physical px along the path per 30 ms tick, in one of the
/// 8 compass directions. Diagonal directions step 0.25/√2 per axis, so the step along the path is
/// 0.25 px in every direction.
/// </summary>
/// <param name="Start">The ring's centre at tick 0, including its random sub-pixel phase.</param>
/// <param name="Direction">The direction of travel.</param>
public sealed record SubPixelAbDriftMotion(PhysicalPoint Start, SubPixelAbDirection Direction) : SubPixelAbMotion
{
    /// <summary>The tick length that drives the drift.</summary>
    public const long TickMilliseconds = 30;

    /// <summary>Physical pixels travelled along the path per tick.</summary>
    public const double StepPixelsPerTick = 0.25;

    private static readonly double Diagonal = 1.0 / Math.Sqrt(2.0);

    private (double X, double Y) UnitVector => Direction switch
    {
        SubPixelAbDirection.East => (1.0, 0.0),
        SubPixelAbDirection.SouthEast => (Diagonal, Diagonal),
        SubPixelAbDirection.South => (0.0, 1.0),
        SubPixelAbDirection.SouthWest => (-Diagonal, Diagonal),
        SubPixelAbDirection.West => (-1.0, 0.0),
        SubPixelAbDirection.NorthWest => (-Diagonal, -Diagonal),
        SubPixelAbDirection.North => (0.0, -1.0),
        SubPixelAbDirection.NorthEast => (Diagonal, -Diagonal),
        _ => throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "Unknown direction."),
    };

    /// <summary>
    /// The ring's position <paramref name="elapsedMs"/> after the interval starts:
    /// <c>Start + 0.25·⌊t/30 ms⌋·dir</c>.
    /// <para>
    /// <paramref name="elapsedMs"/> is measured from the interval's start and is never negative in
    /// production; ⌊t/30⌋ is the integer division's result only for t ≥ 0 (a negative t would
    /// truncate toward zero), so callers must not pre-date the interval.
    /// </para>
    /// </summary>
    /// <param name="elapsedMs">Time since the interval started, in milliseconds; not negative.</param>
    /// <returns>The ring centre for condition A and condition B alike, in physical pixels.</returns>
    public PhysicalPoint PositionAt(long elapsedMs)
    {
        double ticks = elapsedMs / TickMilliseconds;
        var unit = UnitVector;
        return new PhysicalPoint(
            Start.X + StepPixelsPerTick * ticks * unit.X,
            Start.Y + StepPixelsPerTick * ticks * unit.Y);
    }
}

/// <summary>
/// A still park: the ring is drawn at <paramref name="Position"/> for both intervals.
/// <para>
/// The position is the ring's <b>centre</b>, and its fractional part is in [0.3, 0.7] on both axes.
/// Condition A rounds the <b>top-left</b> (<c>centre − sizeDip·scale/2</c>), so the A–B offset is
/// driven by the top-left's fractional part, and the range's guarantee — that the ring cannot sit
/// exactly on a pixel nor exactly half a pixel off by construction — holds only when the physical
/// half-size <c>sizeDip·scale/2</c> is a whole number of device pixels (review 01 F4).
/// </para>
/// <para>
/// <b>Contract for the probe (SP-2):</b> draw a static park with a whole-pixel physical half-size —
/// scale 2.0 with a whole-DIP size, which is the owner's case (250 DIP at 2.0) — or else apply the
/// [0.3, 0.7] fraction to the rounded top-left instead of the centre.
/// </para>
/// </summary>
/// <param name="Position">The ring centre, in physical pixels.</param>
public sealed record SubPixelAbStaticMotion(PhysicalPoint Position) : SubPixelAbMotion;

/// <summary>
/// The live target: a slow dot on a straight path, the same path in both intervals of the trial. The
/// probe ring itself follows the owner's real gaze; the target only tells the eye where to look.
/// </summary>
/// <param name="TargetStart">The target dot's position at the interval's start, in physical pixels.</param>
/// <param name="DirectionX">X component of the unit direction of travel.</param>
/// <param name="DirectionY">Y component of the unit direction of travel.</param>
public sealed record SubPixelAbLiveMotion(
    PhysicalPoint TargetStart,
    double DirectionX,
    double DirectionY) : SubPixelAbMotion
{
    /// <summary>The target's speed along its path, in physical pixels per second.</summary>
    public const double PixelsPerSecond = 20.0;

    /// <summary>
    /// The target dot's position <paramref name="elapsedMs"/> after the interval starts.
    /// </summary>
    /// <param name="elapsedMs">Time since the interval started, in milliseconds.</param>
    /// <returns>The target position, in physical pixels.</returns>
    public PhysicalPoint TargetPositionAt(long elapsedMs)
    {
        double distance = PixelsPerSecond * (elapsedMs / 1000.0);
        return new PhysicalPoint(
            TargetStart.X + distance * DirectionX,
            TargetStart.Y + distance * DirectionY);
    }
}

/// <summary>
/// The deterministic trial plan of the sub-pixel visibility test: which condition runs first in each
/// trial, and the motion parameters each trial runs with.
/// <para>
/// Pure and reproducible: the same seed and screen produce the same schedule, so a logged seed
/// reproduces a session's trial order and parameters exactly. Each block holds exactly 10 trials,
/// exactly 5 of which run condition A first — order bias cannot be turned into a conviction by an
/// "always pick 2" strategy.
/// </para>
/// <para>
/// Positions are physical pixels inside the middle half of <c>screenBoundsPx</c>, so trials stay
/// central and on screen; the schedule is a pure function of (seed, screen), which is why the screen
/// is an input.
/// </para>
/// </summary>
public sealed class SubPixelAbSchedule
{
    /// <summary>Scored trials per block.</summary>
    public const int TrialsPerBlock = 10;

    /// <summary>A-first trials per block — exact counterbalancing.</summary>
    public const int AFirstPerBlock = TrialsPerBlock / 2;

    /// <summary>Lower bound of the still park's fractional part, on each axis.</summary>
    public const double StaticFractionMin = 0.3;

    /// <summary>Upper bound of the still park's fractional part, on each axis.</summary>
    public const double StaticFractionMax = 0.7;

    /// <summary>Fraction of the screen (per axis) the probe's trial positions are drawn from.</summary>
    private const double CentralRegionFraction = 0.5;

    private static readonly SubPixelAbDirection[] Directions = Enum.GetValues<SubPixelAbDirection>();

    private readonly SubPixelAbTrial[] _drift;
    private readonly SubPixelAbTrial[] _static;
    private readonly SubPixelAbTrial[] _live;
    private readonly SubPixelAbTrial[] _session1;

    private SubPixelAbSchedule(
        int seed,
        SubPixelAbTrial[] drift,
        SubPixelAbTrial[] staticBlock,
        SubPixelAbTrial[] live)
    {
        Seed = seed;
        _drift = drift;
        _static = staticBlock;
        _live = live;
        _session1 = [.. drift, .. staticBlock];
    }

    /// <summary>The seed the schedule was created from; recorded with the results for reproduction.</summary>
    public int Seed { get; }

    /// <summary>The drift block in run order.</summary>
    public IReadOnlyList<SubPixelAbTrial> DriftTrials => _drift;

    /// <summary>The static block in run order.</summary>
    public IReadOnlyList<SubPixelAbTrial> StaticTrials => _static;

    /// <summary>The live block in run order.</summary>
    public IReadOnlyList<SubPixelAbTrial> LiveTrials => _live;

    /// <summary>Session 1's trials in run order: the drift block, then the static block.</summary>
    public IReadOnlyList<SubPixelAbTrial> Session1Trials => _session1;

    /// <summary>Session 2's trials in run order: the live block.</summary>
    public IReadOnlyList<SubPixelAbTrial> Session2Trials => _live;

    /// <summary>
    /// Builds the plan for <paramref name="seed"/> on <paramref name="screenBoundsPx"/>.
    /// </summary>
    /// <param name="seed">The randomization seed, logged with the session so the plan can be reproduced.</param>
    /// <param name="screenBoundsPx">The primary screen, in physical pixels; positions land centrally.</param>
    /// <returns>The schedule for the seed.</returns>
    public static SubPixelAbSchedule Create(int seed, PhysicalRect screenBoundsPx)
    {
        if (screenBoundsPx.Width <= 0 || screenBoundsPx.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(screenBoundsPx), screenBoundsPx, "Screen bounds must be positive.");

        var rng = new Random(seed);

        // One RNG stream, drawn block by block in run order, so the whole plan is a deterministic
        // function of (seed, screen).
        var drift = CreateDriftBlock(rng, screenBoundsPx);
        var staticBlock = CreateStaticBlock(rng, screenBoundsPx);
        var live = CreateLiveBlock(rng, screenBoundsPx);

        return new SubPixelAbSchedule(seed, drift, staticBlock, live);
    }

    /// <summary>
    /// The trial to append to the end of its block when <paramref name="voided"/> is repeated after
    /// a void. Order and parameters carry over verbatim: re-drawing either would break the block's
    /// exact 5/5 counterbalancing and the trial's recorded motion.
    /// </summary>
    /// <param name="voided">The trial that was voided and must run again.</param>
    /// <returns>The trial to run in its place.</returns>
    public SubPixelAbTrial Requeue(SubPixelAbTrial voided) => voided;

    private static SubPixelAbTrial[] CreateDriftBlock(Random rng, PhysicalRect screenBoundsPx)
    {
        var orders = ShuffledOrders(rng);
        var trials = new SubPixelAbTrial[TrialsPerBlock];

        for (int i = 0; i < trials.Length; i++)
        {
            var start = RandomPoint(rng, screenBoundsPx);
            var direction = Directions[rng.Next(Directions.Length)];
            trials[i] = new SubPixelAbTrial(
                SubPixelAbBlock.Drift, i, orders[i], new SubPixelAbDriftMotion(start, direction));
        }

        return trials;
    }

    private static SubPixelAbTrial[] CreateStaticBlock(Random rng, PhysicalRect screenBoundsPx)
    {
        var orders = ShuffledOrders(rng);
        var trials = new SubPixelAbTrial[TrialsPerBlock];

        for (int i = 0; i < trials.Length; i++)
        {
            var position = RandomStaticPosition(rng, screenBoundsPx);
            trials[i] = new SubPixelAbTrial(
                SubPixelAbBlock.Static, i, orders[i], new SubPixelAbStaticMotion(position));
        }

        return trials;
    }

    private static SubPixelAbTrial[] CreateLiveBlock(Random rng, PhysicalRect screenBoundsPx)
    {
        var orders = ShuffledOrders(rng);
        var trials = new SubPixelAbTrial[TrialsPerBlock];

        for (int i = 0; i < trials.Length; i++)
        {
            var start = RandomPoint(rng, screenBoundsPx);
            double angle = rng.NextDouble() * Math.Tau;
            trials[i] = new SubPixelAbTrial(
                SubPixelAbBlock.Live, i, orders[i],
                new SubPixelAbLiveMotion(start, Math.Cos(angle), Math.Sin(angle)));
        }

        return trials;
    }

    // Exactly 5 A-first and 5 B-first, then a Fisher-Yates shuffle: the exact balance is what caps
    // an order-picking strategy at 5/10, and the shuffle is what keeps the order unguessable.
    private static SubPixelAbOrder[] ShuffledOrders(Random rng)
    {
        var orders = new SubPixelAbOrder[TrialsPerBlock];
        for (int i = 0; i < AFirstPerBlock; i++) orders[i] = SubPixelAbOrder.AFirst;
        for (int i = AFirstPerBlock; i < orders.Length; i++) orders[i] = SubPixelAbOrder.BFirst;

        for (int i = orders.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (orders[i], orders[j]) = (orders[j], orders[i]);
        }

        return orders;
    }

    private static PhysicalPoint RandomPoint(Random rng, PhysicalRect screenBoundsPx)
    {
        var region = CentralRegion(screenBoundsPx);
        return new PhysicalPoint(
            region.MinX + rng.NextDouble() * region.Width,
            region.MinY + rng.NextDouble() * region.Height);
    }

    // The fractional part is set explicitly on top of a whole-pixel anchor, so it is exactly in
    // [StaticFractionMin, StaticFractionMax) no matter what the screen origin or size is.
    private static PhysicalPoint RandomStaticPosition(Random rng, PhysicalRect screenBoundsPx)
    {
        var region = CentralRegion(screenBoundsPx);
        double x = Math.Floor(region.MinX + rng.NextDouble() * region.Width) + RandomStaticFraction(rng);
        double y = Math.Floor(region.MinY + rng.NextDouble() * region.Height) + RandomStaticFraction(rng);
        return new PhysicalPoint(x, y);
    }

    private static double RandomStaticFraction(Random rng) =>
        StaticFractionMin + rng.NextDouble() * (StaticFractionMax - StaticFractionMin);

    private static (double MinX, double MinY, double Width, double Height) CentralRegion(PhysicalRect screenBoundsPx)
    {
        double insetX = screenBoundsPx.Width * (1.0 - CentralRegionFraction) / 2.0;
        double insetY = screenBoundsPx.Height * (1.0 - CentralRegionFraction) / 2.0;
        return (
            screenBoundsPx.X + insetX,
            screenBoundsPx.Y + insetY,
            screenBoundsPx.Width - 2.0 * insetX,
            screenBoundsPx.Height - 2.0 * insetY);
    }
}
