namespace EyeCtrl.Core.Tests.Diagnostics;

using System.Diagnostics;
using EyeCtrl.Core.Diagnostics;
using NFluent;

public class DispatchLatencyProbeWindowTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    // One sample: enqueued at t-latency, executed at t.
    private static void Sample(DispatchLatencyProbe p, double atMs, double latencyMs)
    {
        p.Enqueue();
        p.Execute(Ms(atMs - latencyMs), Ms(atMs));
    }

    [Fact]
    public void Summarize_CountsOnlyTheWindow_EndInclusive_StartExclusive()
    {
        var p = new DispatchLatencyProbe();
        Sample(p, 1_000, 5);   // outside: (16_000 - 15_000) is exclusive
        Sample(p, 1_001, 7);
        Sample(p, 16_000, 9);  // inside: the end is inclusive
        var s = p.Summarize(Ms(16_000), TimeSpan.FromSeconds(15));
        Check.That(s.Count).IsEqualTo(2);
        Check.That(s.MaxMs).IsCloseTo(9, 0.01);
    }

    [Fact]
    public void Summarize_P95_IsTheLowerMillisecondEdge_LikeTheSessionLine()
    {
        var p = new DispatchLatencyProbe();
        for (int i = 0; i < 19; i++) Sample(p, 100 + i * 30, 3.2);
        Sample(p, 100 + 19 * 30, 16.9);           // 20 samples: p95 = the 19th value
        var s = p.Summarize(Ms(100 + 19 * 30), TimeSpan.FromSeconds(15));
        Check.That(s.P95Ms).IsEqualTo(3);         // ceil(0.95*20)=19th ascending = 3.2 -> 3
        Check.That(s.MaxMs).IsCloseTo(16.9, 0.01);
    }

    [Fact]
    public void Summarize_ReportsTheDeepestQueueSeenAtExecute_AndTheLongestGap()
    {
        var p = new DispatchLatencyProbe();
        // Three enqueued before any executes: a depth of 3 is observed at the first execute.
        long e1 = p.Enqueue(), e2 = p.Enqueue(), e3 = p.Enqueue();
        p.Execute(e1, Ms(1_000));
        p.Execute(e2, Ms(1_010));
        p.Execute(e3, Ms(1_400));                 // a 390 ms gap between executes
        var s = p.Summarize(Ms(1_400), TimeSpan.FromSeconds(15));
        Check.That(s.MaxDepth).IsEqualTo(3);
        Check.That(s.GapMs).IsCloseTo(390, 0.5);
    }

    [Fact]
    public void Summarize_CoverageIsShort_WhenTheSessionIsYoungerThanTheWindow()
    {
        var p = new DispatchLatencyProbe();
        Sample(p, 2_000, 4);
        Sample(p, 6_000, 4);
        var s = p.Summarize(Ms(6_000), TimeSpan.FromSeconds(15));
        Check.That(s.CoverageMs).IsCloseTo(4_000, 0.5);
    }

    [Fact]
    public void Summarize_SurvivesWrapAround_AndKeepsTheNewestEntries()
    {
        var p = new DispatchLatencyProbe();
        for (int i = 0; i < 3_000; i++) Sample(p, i * 5.0, i == 2_999 ? 42 : 1);
        var s = p.Summarize(Ms(2_999 * 5.0), TimeSpan.FromSeconds(15));
        Check.That(s.Count).IsEqualTo(2_400);     // capacity bound
        Check.That(s.MaxMs).IsCloseTo(42, 0.01);
    }

    [Fact]
    public void Summarize_EmptyWindow_IsAllZero()
    {
        var s = new DispatchLatencyProbe().Summarize(Ms(1_000), TimeSpan.FromSeconds(15));
        Check.That(s).IsEqualTo(new DispatchWindowSummary(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void LastLatencyMs_IsTheMostRecentExecutesWait()
    {
        var p = new DispatchLatencyProbe();
        Sample(p, 100, 12.5);
        Check.That(p.LastLatencyMs).IsCloseTo(12.5, 0.01);
    }

    [Fact]
    public void DescribeWindow_UsesTheInvariantFormat()
    {
        var text = DispatchLatencyProbe.DescribeWindow(new DispatchWindowSummary(450, 103.25, 16, 3, 410.5, 15000));
        Check.That(text).IsEqualTo("n=450 max=103.25ms p95=16ms maxQueueDepth=3 gapMs=410.5 coverageMs=15000");
    }
}
