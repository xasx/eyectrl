namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using NFluent;

public class GazeEngineFanOutIsolationTests
{
    [Fact]
    public void AThrowingHandler_DoesNotStarveLaterHandlers()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int reached = 0;

        engine.GazeSampled += _ => throw new InvalidOperationException("boom");
        engine.GazeSampled += _ => reached++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(reached).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingHandler_DoesNotEscapeToTheProvider()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        engine.GazeSampled += _ => throw new InvalidOperationException("boom");

        // Must not throw. Before the fix this propagated out of OnGazePoint into the provider's thread.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
    }

    [Fact]
    public void AThrowingHandler_DoesNotStopTheCursorMoving()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int cursorMoves = 0;

        engine.GazeSampled += _ => throw new InvalidOperationException("boom");
        engine.CursorMoved += _ => cursorMoves++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(cursorMoves).IsEqualTo(1);
    }

    [Fact]
    public void HandlersRun_InRegistrationOrder()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        var order = new List<int>();

        engine.GazeSampled += _ => order.Add(1);
        engine.GazeSampled += _ => order.Add(2);
        engine.GazeSampled += _ => order.Add(3);

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(order).ContainsExactly(1, 2, 3);
    }

    [Fact]
    public void Unsubscribe_RemovesOnlyTheLastMatchingHandler()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int hits = 0;
        Action<GazePoint> handler = _ => hits++;

        engine.GazeSampled += handler;
        engine.GazeSampled += handler;
        engine.GazeSampled += handler;
        engine.GazeSampled -= handler;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        // Multicast -= removes one occurrence, not all three.
        Check.That(hits).IsEqualTo(2);
    }

    [Fact]
    public void AThrowingHandler_StaysSubscribed_AndIsCalledAgain()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int calls = 0;

        engine.GazeSampled += _ => { calls++; throw new InvalidOperationException("boom"); };

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        provider.SimulateGaze(new GazePoint(100, 200, 1_033_333, IsValid: true));

        // No trip-and-disable, deliberately: nothing silently disappears, so no downstream consumer
        // can be left reading a frozen value. The rate limiter is what bounds the logging cost.
        Check.That(calls).IsEqualTo(2);
    }

    [Fact]
    public void AHandlerUnsubscribingItself_DoesNotDisturbTheInFlightPublication()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int firstHits = 0;
        int secondHits = 0;
        Action<GazePoint>? first = null;
        first = _ => { firstHits++; engine.GazeSampled -= first!; };

        engine.GazeSampled += first;
        engine.GazeSampled += _ => secondHits++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        provider.SimulateGaze(new GazePoint(100, 200, 1_033_333, IsValid: true));

        Check.That(firstHits).IsEqualTo(1);   // ran once, then removed itself
        Check.That(secondHits).IsEqualTo(2);  // never skipped
    }

    [Fact]
    public void AThrowingCursorMovedHandler_DoesNotStarveLaterHandlers()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int reached = 0;

        engine.CursorMoved += _ => throw new InvalidOperationException("boom");
        engine.CursorMoved += _ => reached++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(reached).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingCursorMovedHandler_DoesNotStopFixationProcessing()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        engine.CursorMoved += _ => throw new InvalidOperationException("boom");

        // Must not throw: the remainder of OnGazePoint (centroid tracking, fixation) still runs.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
    }

    [Fact]
    public void UnsubscribeWithoutSubscription_IsANoOp()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int hits = 0;
        Action<GazePoint> handler = _ => hits++;

        engine.GazeSampled -= handler;   // never subscribed — must not throw or disturb
        engine.GazeSampled += handler;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(hits).IsEqualTo(1);
    }

    [Fact]
    public void NullAddAndRemove_AreIgnored()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int hits = 0;

        engine.GazeSampled += null!;
        engine.GazeSampled -= null!;
        engine.GazeSampled += _ => hits++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(hits).IsEqualTo(1);
    }

    [Fact]
    public void FaultsAreLogged_ThroughTheReporter_RateLimitedToOnePerWindow()
    {
        // End-to-end wiring: the fault path must reach the logger exactly once per 30 s window.
        // Deleting the _faults.Report call in PublishGazeSampled leaves Faults empty.
        var logger = new CapturingLogger();
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings, logger: logger);
        engine.Start();
        engine.GazeSampled += _ => throw new InvalidOperationException("boom");

        for (int i = 0; i < 5; i++)
            provider.SimulateGaze(new GazePoint(100, 200, 1_000_000 + i, IsValid: true));

        Check.That(logger.Faults).HasSize(1);
    }

    [Fact]
    public void Unsubscribing_DropsTheFaultBookkeeping_SoTheEntryDoesNotOutliveTheHandler()
    {
        // AdaptationCoordinator subscribes and unsubscribes on every adaptation Enable toggle. The
        // reporter's dictionary is keyed on the handler and has no expiry, so the remove accessor is
        // what keeps it from growing one entry per toggle. Observable through the rate limiter:
        // re-subscribing inside the same window reports again, which only happens if the entry went.
        var logger = new CapturingLogger();
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings, logger: logger);
        engine.Start();

        void Throwing(GazePoint _) => throw new InvalidOperationException("boom");

        engine.GazeSampled += Throwing;
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        Check.That(logger.Faults).HasSize(1);

        engine.GazeSampled -= Throwing;
        engine.GazeSampled += Throwing;
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_001, IsValid: true));

        // Same wall-clock window throughout, so a surviving entry would have suppressed this one.
        Check.That(logger.Faults).HasSize(2);
    }

    [Fact]
    public void UnsubscribingOneOfTwoIdenticalHandlers_KeepsTheSurvivorRateLimited()
    {
        // The counterpart: one equal subscription is still live, so its window must NOT be reset —
        // otherwise a duplicate-subscribe/unsubscribe pair becomes a way to defeat the rate limit.
        var logger = new CapturingLogger();
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings, logger: logger);
        engine.Start();

        void Throwing(GazePoint _) => throw new InvalidOperationException("boom");

        engine.GazeSampled += Throwing;
        engine.GazeSampled += Throwing;
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        Check.That(logger.Faults).HasSize(1); // two faults, one window

        engine.GazeSampled -= Throwing;      // one copy remains
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_001, IsValid: true));

        Check.That(logger.Faults).HasSize(1);
    }

    private sealed class CapturingLogger : ILogger<GazeEngine>
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

    private static (FakeGazeProvider Provider, GazeEngine Engine) NewEngine()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);
        engine.Start();
        return (provider, engine);
    }
}
