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

    // ── Fixed-site reporting (providers) ──

    [Fact]
    public void ReportContext_RateLimitsAFixedFaultSite()
    {
        // The provider catches have no per-handler identity to key on, so they key on the message.
        // A pipeline fault repeats on every poll; it must cost one line per window, not one per poll.
        var (sut, emitted, _) = NewReporter();

        for (int i = 0; i < 100; i++)
            sut.Report("Gaze callback threw", new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(1);
        Check.That(emitted[0]).IsEqualTo("Gaze callback threw");
    }

    [Fact]
    public void ReportContext_CountsWhatWasSuppressed_SoABlipIsDistinguishableFromABrokenPipeline()
    {
        var (sut, emitted, clock) = NewReporter();

        for (int i = 0; i < 10; i++)
            sut.Report("Gaze callback threw", new InvalidOperationException("boom"));
        clock.Advance(31);
        sut.Report("Gaze callback threw", new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
        Check.That(emitted[1]).Contains("9");
    }

    [Fact]
    public void ReportContext_AndHandlerReports_DoNotShareAWindow()
    {
        // A string key and a delegate key can never be equal, so the two report shapes cannot
        // silently rate-limit each other.
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        sut.Report("GazeSampled", new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
    }

    // ── Eviction ──

    [Fact]
    public void Forget_DropsTheWindow_SoAResubscribedHandlerReportsAgain()
    {
        // AdaptationCoordinator re-subscribes on every Enable toggle. Without eviction each
        // subscription instance left an entry behind; with it, a fresh subscription also gets a
        // fresh window rather than inheriting the old one's silence.
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        Check.That(emitted).HasSize(1); // second was inside the window

        sut.Forget(Handler);
        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
    }

    [Fact]
    public void Forget_IsANoOp_ForAHandlerThatNeverFaulted()
    {
        var (sut, emitted, _) = NewReporter();

        sut.Forget(Handler);

        Check.That(emitted).IsEmpty();
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
