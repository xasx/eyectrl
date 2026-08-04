namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using NFluent;

public class HandlerFaultReporterTests
{
    private static void Handler() { }
    private static void OtherHandler() { }

    [Fact]
    public void Report_EmitsTheFirstFaultImmediately()
    {
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(1);
        Check.That(emitted[0]).Contains("GazeSampled");
    }

    [Fact]
    public void Report_SuppressesRepeatsInsideTheWindow()
    {
        var (sut, emitted, _) = NewReporter();

        for (int i = 0; i < 100; i++)
            sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(1);
    }

    [Fact]
    public void Report_EmitsAgainAfterTheWindow_AndCountsWhatWasSuppressed()
    {
        var (sut, emitted, clock) = NewReporter();

        for (int i = 0; i < 10; i++)
            sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        clock.Advance(31);
        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
        // 10 in-window reports, of which 1 was emitted, so 9 were suppressed.
        Check.That(emitted[1]).Contains("9");
    }

    [Fact]
    public void Report_RateLimitsEachHandlerIndependently()
    {
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        sut.Report("GazeSampled", OtherHandler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
    }

    [Fact]
    public void Report_DoesNotEscape_WhenTheEmitCallbackThrows()
    {
        // The reporter runs inside a catch on the gaze thread; an emit that throws would re-open
        // the containment it reports into. Must not throw.
        var sut = new HandlerFaultReporter(
            (_, _) => throw new InvalidOperationException("logger exploded"),
            () => 0,
            windowSeconds: 30);

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
    }

    private sealed class FakeClock
    {
        public long Seconds { get; private set; }
        public void Advance(long seconds) => Seconds += seconds;
    }

    private static (HandlerFaultReporter Sut, List<string> Emitted, FakeClock Clock) NewReporter()
    {
        var emitted = new List<string>();
        var clock = new FakeClock();
        var sut = new HandlerFaultReporter(
            (message, _) => emitted.Add(message),
            () => clock.Seconds,
            windowSeconds: 30);
        return (sut, emitted, clock);
    }
}
