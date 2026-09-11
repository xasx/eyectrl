namespace EyeCtrl.Core.Tests.Diagnostics;

using System.Diagnostics;
using EyeCtrl.Core.Diagnostics;

/// <summary>
/// The two Stage-2 UI instruments. Each exists to answer one pre-registered question, and each can
/// return the answer that kills the proposal it was built for — so what is tested here is that the
/// verdict follows the data, in both directions.
/// </summary>
public class UiInstrumentProbeTests
{
    // ── Instrument 1: dispatch latency ────────────────────────────────────────

    [Fact]
    public void Dispatch_RecordsOneLatencyPerExecutedItem()
    {
        var probe = new DispatchLatencyProbe();

        for (int i = 0; i < 5; i++)
            probe.Execute(probe.Enqueue());

        Check.That(probe.Latency.Count).IsEqualTo(5L);
    }

    [Fact]
    public void Dispatch_ConvertsStopwatchTicksToMilliseconds()
    {
        // The positive control for the arithmetic the whole falsifier rests on. The other latency tests
        // inject values straight into the histogram, so they would all still pass if Execute dropped its
        // ticks→ms factor (every latency inflated ~10^7×, so nothing ever looks fast), or subtracted the
        // wrong way round (every latency clamped to 0, so everything looks fast forever). Either slip
        // silently flips the verdict this instrument exists to produce.
        //
        // Deterministic without sleeping: a timestamp exactly 10 ms in the past, built from the
        // stopwatch's own frequency. The only slack is the microseconds between constructing it and
        // Execute reading the clock, which the upper bound absorbs.
        var probe = new DispatchLatencyProbe();
        long tenMillisecondsAgo = Stopwatch.GetTimestamp() - (Stopwatch.Frequency / 100);

        probe.Execute(tenMillisecondsAgo);

        Check.That(probe.Latency.Count).IsEqualTo(1L);
        Check.That(probe.Latency.MaxMilliseconds).IsStrictlyGreaterThan(9.0);    // fails on inversion (clamps to 0)
        Check.That(probe.Latency.MaxMilliseconds).IsStrictlyLessThan(25.0);      // fails on a missing ticks→ms factor
    }

    [Fact]
    public void Dispatch_MeasuresRealElapsedTimeBetweenEnqueueAndExecute()
    {
        // The end-to-end counterpart: a real wait through the real pair, against the monotonic clock.
        // Sleep guarantees AT LEAST its duration, so the lower bound cannot flake; the upper bound is
        // deliberately enormous so a loaded machine cannot fail it while a unit slip still would.
        var probe = new DispatchLatencyProbe();

        long enqueued = probe.Enqueue();
        Thread.Sleep(15);
        probe.Execute(enqueued);

        Check.That(probe.Latency.MaxMilliseconds).IsStrictlyGreaterThan(10.0);
        Check.That(probe.Latency.MaxMilliseconds).IsStrictlyLessThan(30_000.0);
    }

    [Fact]
    public void Dispatch_AnInvertedTimestampClampsToZero_RatherThanReportingNonsense()
    {
        // Documents the failure mode the control above guards: a timestamp from the future (a clock
        // adjustment, or the subtraction the wrong way round) yields a negative span, which
        // DurationHistogram clamps. Worth pinning so the clamp is a deliberate contract rather than an
        // accident that hides an inversion.
        var probe = new DispatchLatencyProbe();

        probe.Execute(Stopwatch.GetTimestamp() + Stopwatch.Frequency);   // one second in the future

        Check.That(probe.Latency.Count).IsEqualTo(1L);
        Check.That(probe.Latency.MaxMilliseconds).IsEqualTo(0.0);
    }

    [Fact]
    public void Dispatch_DepthRisesWithPendingItemsAndFallsAsTheyExecute()
    {
        // The queue-depth proxy is the burst evidence the coalescing hypothesis predicts: posted but
        // not yet run.
        var probe = new DispatchLatencyProbe();

        long a = probe.Enqueue();
        long b = probe.Enqueue();
        long c = probe.Enqueue();
        Check.That(probe.CurrentDepth).IsEqualTo(3);

        probe.Execute(a);
        probe.Execute(b);
        Check.That(probe.CurrentDepth).IsEqualTo(1);

        probe.Execute(c);
        Check.That(probe.CurrentDepth).IsEqualTo(0);
    }

    [Fact]
    public void Dispatch_MaxDepthIsThePeakNotTheCurrent()
    {
        var probe = new DispatchLatencyProbe();

        long a = probe.Enqueue();
        long b = probe.Enqueue();
        probe.Execute(a);
        probe.Execute(b);

        Check.That(probe.CurrentDepth).IsEqualTo(0);
        Check.That(probe.MaxDepth).IsEqualTo(2);
    }

    [Fact]
    public async Task Dispatch_MaxDepthSurvivesConcurrentEnqueues()
    {
        // The peak is recorded with an interlocked compare-exchange, not a read-then-write: a lost update
        // would UNDER-report the peak, which falsely acquits the queue — the wrong direction to be wrong in.
        //
        // Deliberately noted as ONE-SIDED. For the correct implementation this passes deterministically:
        // nothing decrements during the enqueue phase, so the depth reaches exactly the total. Against a
        // naive read-then-write regression it only catches the lost update PROBABILISTICALLY — very
        // likely at this contention, but not guaranteed. The rounds below raise the odds; they do not
        // make it a proof. Treat it as strong evidence, not a deterministic guard.
        var probe = new DispatchLatencyProbe();
        var stamps = new System.Collections.Concurrent.ConcurrentQueue<long>();

        const int rounds = 4, threads = 8, perThread = 250;
        for (int round = 0; round < rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++) stamps.Enqueue(probe.Enqueue());
            })));

            int expectedDepth = (round + 1) * threads * perThread;
            Check.That(probe.CurrentDepth).IsEqualTo(expectedDepth);
            Check.That(probe.MaxDepth).IsEqualTo(expectedDepth);
        }

        while (stamps.TryDequeue(out long stamp)) probe.Execute(stamp);
        Check.That(probe.CurrentDepth).IsEqualTo(0);
        Check.That(probe.MaxDepth).IsEqualTo(rounds * threads * perThread);   // the peak, not the current
    }

    [Fact]
    public void Dispatch_Falsifier_WithdrawsCoalescingWhenP95IsUnderOneFrame()
    {
        // The pre-registered rule, in the direction that KILLS the proposal: if the queue is fast, the
        // UI-coalescing work is withdrawn rather than implemented.
        var probe = new DispatchLatencyProbe();
        for (int i = 0; i < 100; i++) probe.Latency.Record(2);   // 2 ms, comfortably inside a frame

        Check.That(probe.P95IsUnderOneFrame()).IsTrue();
        Check.That(probe.Describe()).Contains("WITHDRAWN");
    }

    [Fact]
    public void Dispatch_Falsifier_DoesNotWithdrawWhenP95ExceedsAFrame()
    {
        // A 10 % slow tail, so the 95th sample of 100 falls inside it. Exactly 5 % would NOT: the 95th
        // sample would still be a fast one and p95 would correctly read as fast — the boundary is worth
        // stating, since it is where a careless test would assert the wrong verdict.
        var probe = new DispatchLatencyProbe();
        for (int i = 0; i < 90; i++) probe.Latency.Record(2);
        for (int i = 0; i < 10; i++) probe.Latency.Record(120);

        Check.That(probe.P95IsUnderOneFrame()).IsFalse();
        Check.That(probe.Describe()).Contains("NOT withdrawn");
    }

    [Fact]
    public void Dispatch_WithNoSamples_ClaimsNothing()
    {
        // An unmeasured session must not read as "fast" — that would withdraw a proposal on no evidence.
        var probe = new DispatchLatencyProbe();

        Check.That(probe.P95IsUnderOneFrame()).IsFalse();
        Check.That(probe.Describe()).Contains("no samples");
    }

    // ── Instrument 2: ring quantization and cadence ───────────────────────────

    [Fact]
    public void Ring_DetectsQuantization_WhenFractionalRequestsLandOnWholePixels()
    {
        // The case that matters: every write asks for a fractional device pixel and the window lands on
        // the rounded whole one. Sub-pixel output is being discarded by the last stage, so no filter can
        // make the ring sub-pixel smooth.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 50; i++)
        {
            double requested = 100 + i * 0.37;
            probe.Record(requested, requested, Math.Round(requested), Math.Round(requested), 1.0, 1.0);
        }

        Check.That(probe.SubPixelSurvives()).IsEqualTo(false);
        Check.That(probe.WholePixelFraction).IsEqualTo(1.0);
        Check.That(probe.MeanAbsResidualDevicePixels).IsStrictlyGreaterThan(0.0);
        Check.That(probe.Describe()).Contains("DISCARDED");
    }

    [Fact]
    public void Ring_DetectsSurvival_WhenFractionalRequestsAreHonoured()
    {
        // The opposite verdict, which drops the transform-based overlay endgame from the roadmap. The
        // instrument has to be able to produce it, or it is not a measurement.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 50; i++)
        {
            double requested = 100 + i * 0.37;
            probe.Record(requested, requested, requested, requested, 1.0, 1.0);
        }

        Check.That(probe.SubPixelSurvives()).IsEqualTo(true);
        Check.That(probe.ExactMatchFraction).IsEqualTo(1.0);
        Check.That(probe.Describe()).Contains("SURVIVES");
    }

    [Fact]
    public void Ring_IsInconclusive_WhenNothingEverAskedForAFractionalPixel()
    {
        // The trap this guards: if every request happens to be a whole pixel, perfect fidelity and total
        // quantization look identical. Claiming either would be a verdict the data cannot support.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 20; i++)
            probe.Record(100 + i, 200 + i, 100 + i, 200 + i, 1.0, 1.0);

        Check.That(probe.SubPixelSurvives()).IsNull();
        Check.That(probe.Describe()).Contains("inconclusive");
    }

    [Fact]
    public void Ring_ComparesInDevicePixels_SoScaleCannotHideTheEffect()
    {
        // At 200 % DPI a half-DIP request IS a whole device pixel, so it should be honoured exactly.
        // Comparing in DIPs would misreport this as a fractional request being rounded away.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 20; i++)
        {
            double requestedDip = 100 + i * 0.5;
            probe.Record(requestedDip, requestedDip, requestedDip * 2.0, requestedDip * 2.0, 2.0, 2.0);
        }

        Check.That(probe.ExactMatchFraction).IsEqualTo(1.0);
        Check.That(probe.MeanAbsResidualDevicePixels).IsEqualTo(0.0);
    }

    [Fact]
    public void Ring_AppliesEachAxisOwnScale()
    {
        // Every other ring case feeds x == y at scaleX == scaleY, so a scaleX/scaleY (or X/Y) swap inside
        // Record passes unnoticed. This case is built so the CORRECT mapping lands every write exactly:
        // distinct axes, distinct scales, and device values that are whole under the right pairing. Swap
        // the scales and the requested device position no longer matches the observed one at all, so the
        // exact-match fraction collapses from 1.0 — the assertion below is what makes the swap visible.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 40; i++)
        {
            double dipX = 100 + i * 2;      // × 1.5 → 150 + 3i, whole
            double dipY = 200 + i * 0.5;    // × 2.0 → 400 + i,  whole

            probe.Record(dipX, dipY, dipX * 1.5, dipY * 2.0, scaleX: 1.5, scaleY: 2.0);
        }

        Check.That(probe.ExactMatchFraction).IsEqualTo(1.0);
        Check.That(probe.MeanAbsResidualDevicePixels).IsEqualTo(0.0);
        Check.That(probe.SubPixelSurvives()).IsNull();   // nothing fractional was ever asked for
    }

    [Fact]
    public void Ring_QuantizationOnOneAxisOnly_IsStillReportedAsQuantized()
    {
        // Asymmetric in the other direction: X loses its fraction, Y is honoured exactly. A probe that
        // looked at only one axis, or averaged them, could miss this entirely.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 40; i++)
        {
            double dipX = 100 + i * 0.37;   // × 1.5 → fractional, rounded away
            double dipY = 200 + i * 0.5;    // × 2.0 → whole, honoured

            probe.Record(dipX, dipY, Math.Round(dipX * 1.5), dipY * 2.0, scaleX: 1.5, scaleY: 2.0);
        }

        Check.That(probe.SubPixelSurvives()).IsEqualTo(false);
        Check.That(probe.MeanAbsResidualDevicePixels).IsStrictlyGreaterThan(0.0);
    }

    [Fact]
    public void Ring_ResidualIsTheWorstAxisNotTheAverage()
    {
        // The per-write residual must be the MAX of the two axes. Averaging would let one good axis mask
        // the other: here X is perfect and Y is off by 0.015 device pixels — the mean (0.0075) sits under
        // the 0.01 exactness threshold and would be scored as an exact match, so a whole session of
        // one-axis quantization could report SURVIVES.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 20; i++)
            probe.Record(10.0, 20.0, 10.0, 20.015, scaleX: 1.0, scaleY: 1.0);

        Check.That(probe.ExactMatchFraction).IsEqualTo(0.0);
        Check.That(probe.MeanAbsResidualDevicePixels).IsCloseTo(0.015, 1e-9);
    }

    [Fact]
    public void Ring_CadenceHistogramMeasuresIntervalsBetweenWrites()
    {
        // Settles the 33 Hz-against-60 Hz beat question. The first write has no predecessor, so n
        // writes produce n-1 intervals.
        var probe = new RingQuantizationProbe();
        for (int i = 0; i < 5; i++)
        {
            probe.Record(i, i, i, i, 1.0, 1.0);
            Thread.Sleep(5);
        }

        Check.That(probe.Count).IsEqualTo(5L);
        Check.That(probe.Intervals.Count).IsEqualTo(4L);
        Check.That(probe.Intervals.MaxMilliseconds).IsStrictlyGreaterThan(0.0);
    }

    [Fact]
    public void Ring_WithNoSamples_ClaimsNothing()
    {
        var probe = new RingQuantizationProbe();

        Check.That(probe.SubPixelSurvives()).IsNull();
        Check.That(probe.Describe()).Contains("no samples");
        Check.That(probe.DescribeCadence()).Contains("no intervals");
    }
}
