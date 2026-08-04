namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;

public class GazeEngineFanOutIsolationTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) { }
        public void LeftClick(PhysicalPoint p) { }
        public void RightClick(PhysicalPoint p) { }
        public void DoubleClick(PhysicalPoint p) { }
        public void MouseDown(PhysicalPoint p) { }
        public void MouseUp(PhysicalPoint p) { }
        public void Scroll(PhysicalPoint p, int dx, int dy) { }
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }

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
        engine.CursorMoved += (_, _) => cursorMoves++;

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

        engine.CursorMoved += (_, _) => throw new InvalidOperationException("boom");
        engine.CursorMoved += (_, _) => reached++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(reached).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingCursorMovedHandler_DoesNotStopFixationProcessing()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        engine.CursorMoved += (_, _) => throw new InvalidOperationException("boom");

        // Must not throw: the remainder of OnGazePoint (centroid tracking, fixation) still runs.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
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
