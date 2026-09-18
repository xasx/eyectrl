namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using NFluent;

// Per-sample fault containment in OnGazePoint: a fault in the pipeline body itself (not in a
// subscriber) must not escape to the provider. On the Tobii provider an escaping throw is caught
// by the poll loop and treated as a DEVICE error — reconnect with backoff — so a data-dependent
// fault (non-finite sample through the total-fail coordinate mapper, zero screen metrics during a
// display transition) recurred on the first sample of every reconnect: an app that stays alive
// with a dead cursor, undetectable by the heartbeat (samples publish before the faulting step).
public class GazeEngineSampleFaultContainmentTests
{
    [Fact]
    public void ThrowingInputSimulator_DoesNotEscapeToTheProvider_NextSampleStillMoves()
    {
        var provider = new FakeGazeProvider();
        var settings = NewDirectSettings();
        var simulator = new ThrowingOnceInputSimulator();
        using var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        // Must not throw: the fault is contained, the sample is skipped.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        // The pipeline keeps running: the NEXT sample reaches the simulator again.
        provider.SimulateGaze(new GazePoint(120, 220, 1_033_000, IsValid: true));

        Check.That(simulator.MoveAttempts).IsEqualTo(2);
        Check.That(simulator.MovedPositions).HasElementThatMatches(p => p.X == 120);
    }

    [Fact]
    public void SustainedFaults_EscalateOncePerEpisode()
    {
        var provider = new FakeGazeProvider();
        var settings = NewDirectSettings();
        var logger = new CapturingLogger();
        using var engine = new GazeEngine(provider, new AlwaysThrowingInputSimulator(), settings, logger: logger);
        engine.Start();

        // Enough faults to cross SustainedSampleFaultThreshold (150). All within one 30s rate
        // window: the per-fault line emits once, the escalation line (its own key) emits once.
        for (var i = 0; i < 160; i++)
            provider.SimulateGaze(new GazePoint(100, 200, 1_000_000 + i, IsValid: true));

        Check.That(logger.Faults).HasSize(2);
        Check.That(logger.Faults[1].Message).Contains("rejecting every sample");
    }

    private static EyeCtrlSettings NewDirectSettings()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        return settings;
    }

    private sealed class ThrowingOnceInputSimulator : IInputSimulator
    {
        public int MoveAttempts { get; private set; }
        public List<PhysicalPoint> MovedPositions { get; } = new();

        public void MoveCursor(PhysicalPoint p)
        {
            MoveAttempts++;
            if (MoveAttempts == 1)
                throw new InvalidOperationException("simulated non-finite coordinate");
            MovedPositions.Add(p);
        }

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

    private sealed class AlwaysThrowingInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) => throw new InvalidOperationException("simulated dead mapper");
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

    private sealed class CapturingLogger : ILogger<GazeEngine>
    {
        public List<(string Message, Exception Exception)> Faults { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Faults.Add((formatter(state, exception), exception));
        }
    }
}
