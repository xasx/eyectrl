namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging;
using NFluent;

public class MouseGazeProviderFaultTests
{
    // Ticks land on pool threads, so both the log list and the call counter are touched from a
    // thread other than the test's. Poll for the condition instead of sleeping a fixed span: a
    // wall-clock delay sized to "should be enough ticks" is what makes this project's other
    // timer-driven tests flake under load.
    private const int TicksToObserve = 5;
    private const int WaitTimeoutMs = 5000;

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<Exception> _faults = [];

        public IReadOnlyList<Exception> Snapshot()
        {
            lock (_gate)
                return [.. _faults];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null) return;
            lock (_gate)
                _faults.Add(exception);
        }
    }

    [Fact]
    public async Task AThrowingSubscriber_IsCaughtAndLogged_NotLeftToKillTheProcess()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        int calls = 0;
        sut.GazePointReceived += _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        };

        sut.Start();
        bool ticked = await WaitUntilAsync(() => Volatile.Read(ref calls) >= TicksToObserve);
        sut.Stop();

        Check.That(ticked).IsTrue();
        // Without the catch, the exception escapes the timer callback and nothing is ever logged.
        // With it, the 30 s window holds five faults down to exactly the first report.
        Check.That(logger.Snapshot()).HasSize(1);
        Check.That(logger.Snapshot()[0]).IsInstanceOf<InvalidOperationException>();
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotStopTheProviderTicking()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        int calls = 0;
        sut.GazePointReceived += _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        };

        sut.Start();
        bool ticked = await WaitUntilAsync(() => Volatile.Read(ref calls) >= TicksToObserve);
        sut.Stop();

        // The finally already re-armed before this change; this guards that the added catch did not
        // break the re-arm. A single tick would satisfy a naive assertion, so require several.
        Check.That(ticked).IsTrue();
        Check.That(Volatile.Read(ref calls)).IsStrictlyGreaterThan(1);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout elapses. Returns whether it
    /// held, so a stalled provider fails on an assertion rather than on a torn-down timer.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + WaitTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return condition();
    }
}
