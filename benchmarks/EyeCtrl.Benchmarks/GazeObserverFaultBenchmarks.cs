using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 1 — prices the CONTAINED-FAULT path of the observer fan-out, which is what decides the fault
/// policy. <see cref="GazeObserverFanOutBenchmarks"/> measures the happy path (where try/catch is
/// expected to be free); this measures what a throwing observer actually costs once caught, and what the
/// steady state looks like after trip-and-disable removes it.
/// </summary>
/// <remarks>
/// WHY THE FREQUENCY IS A PARAMETER RATHER THAN A SEPARATE BENCHMARK. A caught exception costs
/// microseconds, so an observer throwing on every one of 2.5 M samples would run for tens of seconds per
/// operation and dominate the suite's runtime. Varying the throw frequency over a fixed batch keeps every
/// case past BDN's 100 ms MinIterationTime floor (see GazePipelineWithAdaptationBenchmarks.cs:36-41) while
/// letting the marginal cost of one caught exception be read off the slope between parameter values:
/// (Mean[ThrowEvery=n] − Mean[ThrowEvery=0]) ÷ (SamplesPerBatch ÷ n).
///
/// <c>ThrowEvery = 0</c> is the no-fault reference: six healthy observers behind the same try/catch, so
/// the difference against it isolates the fault cost and nothing else.
///
/// The batch, stream cycling, stand-in observers, dead-code-elimination guard and the decision not to pin
/// <c>invocationCount</c> all follow <see cref="GazeObserverFanOutBenchmarks"/>; see its remarks for why
/// each is shaped that way.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeObserverFaultBenchmarks
{
    private const int SamplesPerBatch = 2_500_000;
    private const int StreamLength = 1024;
    private const int StreamMask = StreamLength - 1;

    /// <summary>
    /// Throw on every n-th sample; 0 never throws. 256 yields ~9.8 k caught exceptions per operation,
    /// enough to be visible against the no-fault reference without inflating suite runtime.
    /// </summary>
    [Params(0, 4096, 1024, 256)]
    public int ThrowEvery { get; set; }

    private GazePoint[] _stream = null!;
    private ProtoObservers _observers = null!;
    private IProtoObserver[] _registered = null!;
    private int[] _consecutiveFaults = null!;

    // Post-trip steady state: the five survivors, with the faulting observer already unregistered.
    private ProtoObservers _tripped = null!;
    private IProtoObserver[] _trippedRegistered = null!;
    private int[] _trippedFaults = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stream = GazeStreamGenerator.GenerateFixation(length: StreamLength);

        _observers = new ProtoObservers(throwEvery: ThrowEvery);
        _registered = _observers.All;
        _consecutiveFaults = new int[_registered.Length];

        _tripped = new ProtoObservers(count: 5);
        _trippedRegistered = _tripped.All;
        _trippedFaults = new int[_trippedRegistered.Length];
    }

    /// <summary>
    /// Six observers behind per-observer try/catch, one of them throwing every
    /// <see cref="ThrowEvery"/>-th sample. At <c>ThrowEvery = 0</c> this is the no-fault reference.
    /// </summary>
    [Benchmark(Baseline = true)]
    public long ContainedFault()
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
    /// The steady state trip-and-disable buys: the faulting observer is gone, five healthy ones remain.
    /// Independent of <see cref="ThrowEvery"/> by construction — it is repeated across the parameter
    /// values only because BDN pairs benchmarks with every parameter, and the repetition doubles as a
    /// run-to-run consistency check on the harness itself.
    /// </summary>
    [Benchmark]
    public long AfterTrip_FiveHealthy()
    {
        var stream = _stream;
        var observers = _trippedRegistered;
        var faults = _trippedFaults;
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
        return _tripped.Checksum();
    }
}
