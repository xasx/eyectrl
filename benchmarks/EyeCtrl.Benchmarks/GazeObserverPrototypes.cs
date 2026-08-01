using EyeCtrl.Core.Models;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Prototype observer contract for the fan-out benchmarks, exposing both calling conventions under test
/// (by-value and <c>in</c>) so the 32-byte <see cref="GazePoint"/> copy can be priced.
/// </summary>
/// <remarks>
/// Nothing in this file is a proposal for src/. These types exist so the hub design can be measured
/// before it is specced. See <see cref="GazeObserverFanOutBenchmarks"/> for what is being isolated.
/// </remarks>
internal interface IProtoObserver
{
    /// <summary>Sample delivered by value.</summary>
    void OnGazeSample(GazePoint sample);

    /// <summary>Sample delivered by readonly reference.</summary>
    void OnGazeSampleIn(in GazePoint sample);

    /// <summary>Accumulated state, folded into a checksum so the JIT cannot elide the body.</summary>
    long State { get; }
}

/// <summary>Stand-in for AdaptationCoordinator: one volatile write of the sample timestamp.</summary>
internal sealed class TimestampObserver : IProtoObserver
{
    private long _latest;
    public void OnGazeSample(GazePoint sample) => Volatile.Write(ref _latest, sample.TimestampMicroseconds);
    public void OnGazeSampleIn(in GazePoint sample) => Volatile.Write(ref _latest, sample.TimestampMicroseconds);
    public long State => Volatile.Read(ref _latest);
}

/// <summary>Stand-in for HeartbeatPublisher: an Interlocked exchange, payload otherwise ignored.</summary>
internal sealed class ArrivalTickObserver : IProtoObserver
{
    private long _ticks;
    public void OnGazeSample(GazePoint sample) => Interlocked.Exchange(ref _ticks, sample.TimestampMicroseconds);
    public void OnGazeSampleIn(in GazePoint sample) => Interlocked.Exchange(ref _ticks, sample.TimestampMicroseconds);
    public long State => Interlocked.Read(ref _ticks);
}

/// <summary>Stand-in for GazeQualityAggregator: validity counting, no timestamp windowing.</summary>
internal sealed class ValidityObserver : IProtoObserver
{
    private long _valid;
    private long _invalid;
    public void OnGazeSample(GazePoint sample) { if (sample.IsValid) _valid++; else _invalid++; }
    public void OnGazeSampleIn(in GazePoint sample) { if (sample.IsValid) _valid++; else _invalid++; }
    public long State => _valid - _invalid;
}

/// <summary>Stand-in for FatigueMonitor: running positional sums (O(1), deliberately unwindowed).</summary>
internal sealed class DispersionObserver : IProtoObserver
{
    private double _sumX;
    private double _sumY;
    private long _count;
    public void OnGazeSample(GazePoint sample) => Accumulate(sample.X, sample.Y, sample.IsValid);
    public void OnGazeSampleIn(in GazePoint sample) => Accumulate(sample.X, sample.Y, sample.IsValid);

    private void Accumulate(double x, double y, bool valid)
    {
        if (!valid) return;
        _sumX += x;
        _sumY += y;
        _count++;
    }

    public long State => _count + (long)(_sumX + _sumY);
}

/// <summary>Stand-in for ToolbarAutoHideController: a rect containment test.</summary>
internal sealed class RectContainsObserver : IProtoObserver
{
    private const double Left = 0, Top = 0, Right = 1920, Bottom = 80;
    private long _hits;
    public void OnGazeSample(GazePoint sample) => Test(sample.X, sample.Y, sample.IsValid);
    public void OnGazeSampleIn(in GazePoint sample) => Test(sample.X, sample.Y, sample.IsValid);

    private void Test(double x, double y, bool valid)
    {
        if (valid && x >= Left && x <= Right && y >= Top && y <= Bottom) _hits++;
    }

    public long State => _hits;
}

/// <summary>Stand-in for QuickActionsSummonController: a screen-edge proximity test.</summary>
internal sealed class EdgeProximityObserver : IProtoObserver
{
    private const double EdgeThreshold = 8.0;
    private long _past;
    public void OnGazeSample(GazePoint sample) => Test(sample.X, sample.IsValid);
    public void OnGazeSampleIn(in GazePoint sample) => Test(sample.X, sample.IsValid);

    private void Test(double x, bool valid)
    {
        if (valid && x <= EdgeThreshold) _past++;
    }

    public long State => _past;
}

/// <summary>
/// An observer that throws on every <c>throwEvery</c>-th sample. Used to price the contained-fault path
/// and, by varying the frequency, to derive the marginal cost of one caught exception.
/// </summary>
internal sealed class ThrowingObserver : IProtoObserver
{
    private readonly int _throwEvery;
    private long _seen;

    /// <param name="throwEvery">Throw on every n-th sample; 0 disables throwing entirely.</param>
    public ThrowingObserver(int throwEvery) => _throwEvery = throwEvery;

    public void OnGazeSample(GazePoint sample) => Tick();
    public void OnGazeSampleIn(in GazePoint sample) => Tick();

    private void Tick()
    {
        _seen++;
        if (_throwEvery > 0 && _seen % _throwEvery == 0)
            throw new InvalidOperationException("prototype observer fault");
    }

    public long State => _seen;
}

/// <summary>
/// The six stand-ins for the six real GazeSampled subscribers, plus a checksum that keeps their work
/// observable to the JIT.
/// </summary>
/// <remarks>
/// SIX DISTINCT TYPES, NOT SIX INSTANCES OF ONE — six instances of a single class would let the JIT
/// guarded-devirtualize the interface call and understate dispatch cost. Production's call site is
/// megamorphic, so this one is too.
/// </remarks>
internal sealed class ProtoObservers
{
    private readonly IProtoObserver[] _all;

    /// <param name="count">How many of the six to register (used for the post-trip steady state).</param>
    /// <param name="throwEvery">When &gt; 0, the third observer is replaced by a <see cref="ThrowingObserver"/>.</param>
    public ProtoObservers(int count = 6, int throwEvery = 0)
    {
        IProtoObserver[] full =
        [
            new TimestampObserver(),
            new ArrivalTickObserver(),
            throwEvery > 0 ? new ThrowingObserver(throwEvery) : new ValidityObserver(),
            new DispersionObserver(),
            new RectContainsObserver(),
            new EdgeProximityObserver(),
        ];
        _all = full[..count];
    }

    /// <summary>The registered observers, in order.</summary>
    public IProtoObserver[] All => _all;

    /// <summary>Folds every observer's state together so no body can be elided as dead code.</summary>
    public long Checksum()
    {
        long acc = 0;
        for (int i = 0; i < _all.Length; i++)
            acc ^= _all[i].State;
        return acc;
    }
}
