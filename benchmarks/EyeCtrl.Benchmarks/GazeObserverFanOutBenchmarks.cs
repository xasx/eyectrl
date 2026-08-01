using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 1 — prices the candidate designs for a gaze-observer fan-out ("the hub") BEFORE any of them is
/// committed to src. The baseline is the mechanism in production today: a bare multicast
/// <c>event Action&lt;GazePoint&gt;</c>, which is what <c>GazeEngine.GazeSampled</c> is
/// (src/EyeCtrl.Core/GazeEngine.cs:65, invoked at :230). Each subsequent benchmark adds exactly one
/// design element, so the incremental cost of each can be read off the ratio column independently:
/// <list type="bullet">
/// <item>by-value vs <c>in</c> for the 32-byte <see cref="GazePoint"/> struct,</item>
/// <item>per-observer try/catch (claimed free on the happy path — this checks it),</item>
/// <item>a consecutive-fault counter reset per observer per sample,</item>
/// <item>an always-on vs sampled <see cref="Stopwatch"/> slow-observer watchdog,</item>
/// <item>the zero-observer path, i.e. what the mechanism costs when nothing is plugged in.</item>
/// </list>
/// The contained-fault path is priced separately in <see cref="GazeObserverFaultBenchmarks"/>.
/// </summary>
/// <remarks>
/// OBSERVERS ARE DISPATCH STAND-INS, NOT REIMPLEMENTATIONS. Each does O(1) work sized to what the real
/// subscriber does per sample; see GazeObserverPrototypes.cs. They deliberately do NOT window on the
/// sample timestamp, because the stream is cycled (below) and a timestamp-windowing observer would
/// measure its clock-restart branch instead of its steady-state one — the trap documented in
/// GazePipelineWithAdaptationBenchmarks.cs:18-22. What is measured here is the fan-out, not the observers.
///
/// BATCH SIZE AND CYCLING. BDN enforces a 100 ms MinIterationTime floor;
/// GazePipelineWithAdaptationBenchmarks.cs:36-41 records that a 100 k-sample batch fell far under it and
/// went bimodal, reporting numbers that looked precise and were not. At a few tens of ns per sample,
/// 2.5 M samples lands comfortably past the floor. Unlike that benchmark this one does not need 2.5 M
/// *distinct* samples — the stand-ins are timestamp-insensitive by construction — so a 1024-sample stream
/// is cycled with a mask, keeping the hot array in L1 so the numbers reflect dispatch rather than memory
/// bandwidth, and avoiding that benchmark's ~80 MB array. The mask costs one AND per iteration,
/// identically in every benchmark, so it cannot bias the comparison.
///
/// NO PINNED invocationCount. GazePipelineWithAdaptationBenchmarks pins <c>invocationCount: 1</c> because
/// replaying its stream twice would measure the clock-restart branch; that reason does not apply here, as
/// the stand-ins are cycle-safe by construction. Letting BDN choose the invocation count is what keeps
/// iterations above the MinIterationTime floor — pinning it to 1 would put a ~30 ms iteration back under
/// the floor and reintroduce exactly the bimodality cited above.
///
/// DEAD-CODE ELIMINATION. Every benchmark returns a checksum folded from the observers' accumulated
/// state. Without it the JIT is free to elide observer bodies whose results are never read.
///
/// SCOPE. This measures fan-out CPU cost on one thread. It does NOT capture the contention that makes a
/// slow observer dangerous in production: the real fan-out runs inside the Tobii provider's <c>_lock</c>
/// on the poll thread (TobiiStreamEngineProvider.cs:185, reached from process_callbacks inside
/// <c>lock (_lock)</c>), so a blocking observer also stalls Stop() and TryGetLatestStatus. No
/// microbenchmark shows that; it is an argument for the trip policy, not something priced here.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeObserverFanOutBenchmarks
{
    private const int SamplesPerBatch = 2_500_000;
    private const int StreamLength = 1024;              // power of two — cycled with StreamMask
    private const int StreamMask = StreamLength - 1;

    /// <summary>Watchdog sampling interval for the sampled-stopwatch variant (power of two).</summary>
    private const int WatchdogEvery = 64;

    /// <summary>Elapsed-ticks threshold above which an observer would be reported slow.</summary>
    private const long SlowObserverTicks = 10_000;

    private GazePoint[] _stream = null!;

    // One set of six observers, shared by every strategy so the comparison isolates dispatch alone.
    private ProtoObservers _observers = null!;

    private Action<GazePoint>? _multicast;
    private IProtoObserver[] _registered = null!;
    private IProtoObserver[] _empty = null!;
    private int[] _consecutiveFaults = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stream = GazeStreamGenerator.GenerateFixation(length: StreamLength);
        _observers = new ProtoObservers();
        _registered = _observers.All;
        _empty = [];
        _consecutiveFaults = new int[_registered.Length];

        // Baseline: the production mechanism — a multicast delegate over the same six targets.
        _multicast = null;
        foreach (var observer in _registered)
        {
            var captured = observer;
            _multicast += sample => captured.OnGazeSample(sample);
        }
    }

    /// <summary>
    /// The mechanism in production today: <c>GazeSampled?.Invoke(gaze)</c>. No fault isolation — one
    /// throwing subscriber aborts the remaining ones and escapes onto the gaze thread.
    /// </summary>
    [Benchmark(Baseline = true)]
    public long A_BareMulticastEvent()
    {
        var stream = _stream;
        var multicast = _multicast!;
        for (int i = 0; i < SamplesPerBatch; i++)
            multicast(stream[i & StreamMask]);
        return _observers.Checksum();
    }

    /// <summary>Hub as an interface list, sample passed by value (32-byte struct copy per observer).</summary>
    [Benchmark]
    public long B_HubByValue()
    {
        var stream = _stream;
        var observers = _registered;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            var sample = stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
                observers[k].OnGazeSample(sample);
        }
        return _observers.Checksum();
    }

    /// <summary>
    /// Same, with the sample passed by <c>in</c> — mirroring the house convention already used for
    /// <c>IStruggleTelemetrySink.Report(in DwellOutcome)</c>.
    /// </summary>
    [Benchmark]
    public long C_HubByIn()
    {
        var stream = _stream;
        var observers = _registered;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
                observers[k].OnGazeSampleIn(in sample);
        }
        return _observers.Checksum();
    }

    /// <summary>Adds per-observer try/catch — the isolation that stops one fault starving the rest.</summary>
    [Benchmark]
    public long D_HubByIn_TryCatch()
    {
        var stream = _stream;
        var observers = _registered;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
            {
                try { observers[k].OnGazeSampleIn(in sample); }
                catch { /* fault path priced in GazeObserverFaultBenchmarks */ }
            }
        }
        return _observers.Checksum();
    }

    /// <summary>
    /// Adds the consecutive-fault counter for trip-and-disable: an unconditional reset-to-zero per
    /// observer per sample. Written naively (always store) rather than test-then-store, so what gets
    /// measured is the cost of the straightforward implementation.
    /// </summary>
    [Benchmark]
    public long E_HubByIn_TryCatch_TripCounter()
    {
        var stream = _stream;
        var observers = _registered;
        var faults = _consecutiveFaults;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
            {
                try
                {
                    observers[k].OnGazeSampleIn(in sample);
                    faults[k] = 0;
                }
                catch { faults[k]++; }
            }
        }
        return _observers.Checksum();
    }

    /// <summary>
    /// Adds an always-on slow-observer watchdog: two <see cref="Stopwatch.GetTimestamp"/> calls per
    /// observer per sample — twelve clock reads per sample at six observers. This is the variant suspected
    /// of costing more than the entire rest of the gaze path.
    /// </summary>
    [Benchmark]
    public long F_TripCounter_StopwatchAlways()
    {
        var stream = _stream;
        var observers = _registered;
        var faults = _consecutiveFaults;
        long slow = 0;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
            {
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    observers[k].OnGazeSampleIn(in sample);
                    faults[k] = 0;
                    if (Stopwatch.GetTimestamp() - t0 > SlowObserverTicks) slow++;
                }
                catch { faults[k]++; }
            }
        }
        return _observers.Checksum() + slow;
    }

    /// <summary>
    /// The mitigation: watchdog only on every <see cref="WatchdogEvery"/>th sample, amortising the clock
    /// reads while still catching a persistently slow observer.
    /// </summary>
    [Benchmark]
    public long G_TripCounter_StopwatchSampled()
    {
        var stream = _stream;
        var observers = _registered;
        var faults = _consecutiveFaults;
        long slow = 0;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            bool timed = (i & (WatchdogEvery - 1)) == 0;
            for (int k = 0; k < observers.Length; k++)
            {
                try
                {
                    if (timed)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        observers[k].OnGazeSampleIn(in sample);
                        faults[k] = 0;
                        if (Stopwatch.GetTimestamp() - t0 > SlowObserverTicks) slow++;
                    }
                    else
                    {
                        observers[k].OnGazeSampleIn(in sample);
                        faults[k] = 0;
                    }
                }
                catch { faults[k]++; }
            }
        }
        return _observers.Checksum() + slow;
    }

    /// <summary>
    /// Zero observers registered — what the mechanism costs when nothing is plugged in. This is the floor
    /// the design must not exceed for a user who enables no optional features.
    /// </summary>
    [Benchmark]
    public long H_HubEmpty()
    {
        var stream = _stream;
        var observers = _empty;
        long acc = 0;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < observers.Length; k++)
                observers[k].OnGazeSampleIn(in sample);
            acc += sample.TimestampMicroseconds & 1;
        }
        return acc;
    }
}
