namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// S8 B11: every drag end writes exactly ONE <c>Drag: end</c> Information line naming its cause.
/// Before, a drop and a cancel both ended in "State → FreeLook", so the sitting could not tell the
/// near-miss drop of row B11 from a cancel. Each path is driven through its real sender: the ✕
/// button's own definition, the ESC hatch, the lost-gaze reason, Pause, and a real drop.
/// <para>
/// The process-global Serilog logger is captured, which is why this class sits in the exclusive
/// <c>WpfApplication</c> collection (xUnit v3 runs a non-parallel collection alone).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class DragEndLogTests
{
    private static readonly PhysicalPoint Origin = new(210, 120);

    [StaTheory]
    [InlineData("cancel-button")]
    [InlineData("esc")]
    [InlineData("lost-gaze")]
    [InlineData("pause")]
    public void EachCancelPath_LogsExactlyOneEndLine_WithItsCause(string cause)
    {
        // Mutants: the ✕'s reason Unknown (M6) → "cause=unknown"; the Pause path logs no end line
        // (M7) → no line.
        using var h = new Harness();
        h.StartDrag();

        switch (cause)
        {
            case "cancel-button":
                DragCoordinator.CreateCancelButtonDefinition(600).Activate(h.Context.Messenger);
                break;
            case "esc":
                ((TestFakes.FakeEscapeHatch)h.Context.EscapeHatch).Press();
                break;
            case "lost-gaze":
                h.Context.Messenger.Send(new DragCancelMessage(DragCancelReason.LostGaze));
                break;
            case "pause":
                h.Context.InteractionManager.TogglePause();
                break;
        }
        h.Pump();

        Check.That(h.Input.MouseUpCount).IsEqualTo(1);
        var lines = h.EndLines();
        Check.WithCustomMessage($"{cause}: expected exactly one end line, got [{string.Join(" | ", lines)}].")
             .That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith($"Drag: end cause={cause} ");
        Check.That(lines[0]).Contains($"origin=({Origin.X:F0}, {Origin.Y:F0})");
        Check.That(lines[0]).Contains("exitEntries=0");
    }

    [StaFact]
    public void ADrop_LogsOneEndLine_WithItsDiagnostics_AndALateCancelLogsNothing()
    {
        using var h = new Harness();
        h.StartDrag();

        var cancelCentre = DragCoordinator.ExitAnchorPhysical
            + new PhysicalPoint(0, DragCoordinator.CancelButtonVerticalOffsetPhysical);
        var spot = new PhysicalPoint(cancelCentre.X - 700, cancelCentre.Y);
        h.Gaze(new PhysicalPoint(cancelCentre.X - 1400, cancelCentre.Y), -30_000); // arms the drop
        h.Gaze(cancelCentre, 0);                                                    // one ✕ visit
        for (long t = 30_000; t <= 1_200_000; t += 30_000)
            h.Gaze(spot, t);
        h.Pump();

        // A cancel that lands behind the drop (queued before the drop's teardown ran) is not a
        // second drag end. Sent directly: the ESC hatch's own state guard would drop it earlier.
        h.Context.Messenger.Send(new DragCancelMessage(DragCancelReason.Esc));
        h.Pump();
        Check.That(h.Input.MouseUpCount).IsEqualTo(1);

        var lines = h.EndLines();
        Check.WithCustomMessage($"expected exactly one end line, got [{string.Join(" | ", lines)}].")
             .That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("Drag: end cause=drop trigger=");
        Check.That(lines[0]).Contains($"raw=({spot.X:F0}, {spot.Y:F0})");
        Check.That(lines[0]).Contains($"final=({spot.X:F0}, {spot.Y:F0})");
        Check.That(lines[0]).Contains($"disc=({spot.X:F0}, {spot.Y:F0})");
        Check.That(lines[0]).Contains("exitEntries=1");
        Check.That(h.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point).IsEqualTo(spot);

        Check.WithCustomMessage("the ✕ visit wrote no Debug entry line.")
             .That(h.Sink.Messages.Count(m => m.StartsWith("Drag: exit entered zone=cancel ", StringComparison.Ordinal)))
             .IsEqualTo(1);
    }

    [StaFact]
    public void TheVmOnlyResetAfterAPause_IsNotASecondEnd()
    {
        // ResetDragTrailVmToIdle's Receive(new DragCancelMessage()) is not a drag end.
        using var h = new Harness();
        h.StartDrag();
        h.Context.InteractionManager.TogglePause();
        h.Pump();
        h.Context.InteractionManager.TogglePause();   // resume
        h.Pump();

        Check.That(h.EndLines()).HasSize(1);
        Check.That(h.EndLines()[0]).StartsWith("Drag: end cause=pause ");
    }

    private sealed class Harness : IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly DragCoordinator _drag;

        internal Harness()
        {
            Sink = new CapturingSink();
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(Sink).CreateLogger();
            Input = new TestFakes.FakeInputSimulator();
            var (madeContext, _) = TestFakes.MakeDragContextWithBar(Input);
            Context = madeContext with { Dispatcher = Dispatcher.CurrentDispatcher };
            _drag = new DragCoordinator { CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay() };
            _drag.Initialize(Context);
        }

        internal CapturingSink Sink { get; }
        internal TestFakes.FakeInputSimulator Input { get; }
        internal EyeCtrl.App.Services.EventCoordinationContext Context { get; }

        internal void StartDrag()
        {
            var im = Context.InteractionManager;
            im.OnFixationStarted(Origin);
            im.OnActivationButtonDwelled();
            im.OnActionSelected(InteractionAction.Drag);
            Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);
            Check.That(_drag.LastSetupFailure).IsNull();
        }

        internal void Gaze(PhysicalPoint at, long t) =>
            Context.Messenger.Send(new GazeCursorMovedMessage(at, t));

        internal void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        internal List<string> EndLines() =>
            Sink.Messages.Where(m => m.StartsWith("Drag: end", StringComparison.Ordinal)).ToList();

        public void Dispose()
        {
            _drag.Dispose();
            Log.Logger = _previous;
        }
    }

    /// <summary>
    /// Captures the lines written through the process-global Serilog logger, rendered as the app's
    /// file sink renders them (<c>{Message:lj}</c>: strings unquoted), so the assertions read the
    /// line the owner's log will actually contain.
    /// </summary>
    internal sealed class CapturingSink : ILogEventSink
    {
        private static readonly Serilog.Formatting.Display.MessageTemplateTextFormatter Formatter =
            new("{Message:lj}");

        public List<string> Messages { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            using var writer = new System.IO.StringWriter();
            Formatter.Format(logEvent, writer);
            lock (Messages) Messages.Add(writer.ToString());
        }
    }
}
