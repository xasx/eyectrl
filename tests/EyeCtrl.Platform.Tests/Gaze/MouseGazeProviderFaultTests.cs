namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging;
using NFluent;

public class MouseGazeProviderFaultTests
{
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<Exception> Faults = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Faults.Add(exception);
        }
    }

    [Fact]
    public async Task AThrowingSubscriber_IsCaughtAndLogged_NotLeftToKillTheProcess()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        sut.GazePointReceived += _ => throw new InvalidOperationException("boom");

        sut.Start();
        await Task.Delay(200);
        sut.Stop();

        // Without the catch, the exception escapes the timer callback and nothing is ever logged.
        // The rate limiter (30 s window) holds the flood to exactly the first fault.
        Check.That(logger.Faults).HasSize(1);
        Check.That(logger.Faults[0]).IsInstanceOf<InvalidOperationException>();
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotStopTheProviderTicking()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        int calls = 0;
        sut.GazePointReceived += _ => { calls++; throw new InvalidOperationException("boom"); };

        sut.Start();
        await Task.Delay(200);
        sut.Stop();

        // The finally already re-armed before this change; this guards that the added catch did not
        // break the re-arm.
        Check.That(calls > 1).IsTrue();
    }
}
