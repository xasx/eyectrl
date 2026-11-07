namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
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

        // fixup-02: every point is a fraction of the REAL primary screen. The fixed offsets this
        // used (✕ − 1400 px, ✕ − 700 px) put the arming sample off-screen on CI's smaller screen,
        // where it was an off-screen Exit, so the ✕ sample was no new entry and wrote no zone line.
        var screen = EyeCtrl.App.Services.ScreenGeometry.PrimaryScreenBoundsPhysical();
        var cancelCentre = DragCoordinator.ExitAnchorPhysical
            + new PhysicalPoint(0, DragCoordinator.CancelButtonVerticalOffsetPhysical);
        var arm = new PhysicalPoint(screen.X + (0.15 * screen.Width), cancelCentre.Y);
        var spot = new PhysicalPoint(screen.X + (0.45 * screen.Width), cancelCentre.Y);
        h.RequireOnScreenClearOfExitsAndEdges("the arming sample", arm);
        h.RequireOnScreenClearOfExitsAndEdges("the rest", spot);
        h.RequireFarApart("the arming sample", arm, "the rest", spot);
        h.Gaze(arm, -30_000);                                                       // arms the drop
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
        Check.That(lines[0]).Contains(" magnet=none ");
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
    public void AQueuedDrop_BehindAPauseCancel_LogsNoSecondEndLine()
    {
        // fixup-01 L2: the drop side of the once-per-drag guard. The view model sends the drop from
        // gaze processing and the coordinator only QUEUES OnDragDrop; a Pause toggled before that
        // item runs cancels synchronously (cause=pause). The queued drop then runs: its release is
        // a no-op, and only the guard stops a second "cause=drop" line. Mutant X6 (the drop path
        // ignores _dragEndPending) → two end lines.
        using var h = new Harness();
        h.StartDrag();
        var drops = 0;
        var recipient = new object();
        h.Context.Messenger.Register<object, DragDropMessage>(recipient, (_, _) => drops++);

        var screen = EyeCtrl.App.Services.ScreenGeometry.PrimaryScreenBoundsPhysical();
        var spot = new PhysicalPoint(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 2));
        var arm = new PhysicalPoint(screen.X + (screen.Width / 4), screen.Y + (screen.Height / 4));
        h.RequireOnScreenClearOfExitsAndEdges("the arming sample", arm);
        h.RequireOnScreenClearOfExitsAndEdges("the rest", spot);
        h.RequireFarApart("the arming sample", arm, "the rest", spot);
        h.Gaze(arm, -30_000);
        for (long t = 0; t <= 1_200_000 && drops == 0; t += 30_000)
            h.Gaze(spot, t);
        Check.WithCustomMessage("the rest never sent its drop.").That(drops).IsEqualTo(1);
        Check.WithCustomMessage("the drop's teardown ran before the Pause (nothing was queued).")
             .That(h.Input.MouseUpCount).IsEqualTo(0);

        h.Context.InteractionManager.TogglePause();   // runs before the queued drop
        h.Pump();                                       // now the queued drop runs

        var lines = h.EndLines();
        Check.WithCustomMessage($"expected exactly one end line, got [{string.Join(" | ", lines)}].")
             .That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("Drag: end cause=pause ");
        Check.That(h.Input.MouseUpCount).IsEqualTo(1);
        Check.That(h.Input.ButtonsBalanced).IsTrue();
        GC.KeepAlive(recipient);
    }

    [StaFact]
    public void ATopEdgeDrop_ReleasesOnTheTopEdge_AndLogsMagnetTop()
    {
        // S8 B10 end to end: the coordinator publishes the real primary screen, the drop lands
        // half a disc radius below its top edge, and the MouseUp — the release Windows' snap reads —
        // reaches the input simulator AT the edge. Mutants: no magnet (N1) → the release stays
        // below the edge; "magnet=" always none (N7) → the line says none.
        using var h = new Harness();
        h.StartDrag();

        var screen = EyeCtrl.App.Services.ScreenGeometry.PrimaryScreenBoundsPhysical();
        var r = h.Context.DragTrailVM.ReleaseRadiusPhysical;
        var rest = new PhysicalPoint(screen.X + (screen.Width / 3), screen.Y + (0.5 * r));
        var arm = new PhysicalPoint(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 2));
        h.RequireOnScreenClearOfExitsAndEdges("the arming sample", arm);
        h.RequireOnScreenClearOfExitsAndEdges("the rest", rest, magnetEdgeExpected: true);
        h.RequireFarApart("the arming sample", arm, "the rest", rest);
        h.Gaze(arm, -30_000);
        for (long t = 0; t <= 1_200_000; t += 30_000)
            h.Gaze(rest, t);
        h.Pump();

        Check.WithCustomMessage("the MouseUp did not reach the top edge.")
             .That(h.Input.ButtonEvents.Single(e => e.Action == "MouseUp").Point)
             .IsEqualTo(new PhysicalPoint(rest.X, screen.Y));
        var lines = h.EndLines();
        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains(" magnet=top ");
        Check.That(lines[0]).Contains($"raw=({rest.X:F0}, {rest.Y:F0})");
        Check.That(lines[0]).Contains($"clamped=({rest.X:F0}, {rest.Y:F0})");
        Check.That(lines[0]).Contains($"final=({rest.X:F0}, {screen.Y:F0})");
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

        /// <summary>
        /// fixup-02 precondition: a scenario point must be on the real primary screen, outside both
        /// drag exit zones and — unless the row means a magnet edge — farther than one disc radius
        /// from every edge, whatever the screen size. A screen too small for the scenario then
        /// fails here, by name, instead of as a misleading assertion later (the CI lesson: an
        /// off-screen arming sample was an Exit and silently ate the ✕ entry).
        /// </summary>
        internal void RequireOnScreenClearOfExitsAndEdges(
            string name, PhysicalPoint p, bool magnetEdgeExpected = false)
        {
            var s = EyeCtrl.App.Services.ScreenGeometry.PrimaryScreenBoundsPhysical();
            var anchor = DragCoordinator.ExitAnchorPhysical;
            var zone = DragCoordinator.ExitZoneRadiusPhysical;
            var r = Context.DragTrailVM.ReleaseRadiusPhysical;
            var where = $"{name} ({p.X:F0}, {p.Y:F0}) on the {s.Width:F0}×{s.Height:F0} px screen";

            Check.WithCustomMessage($"precondition: {where} is off-screen.")
                 .That(p.X >= s.X && p.X <= s.X + s.Width && p.Y >= s.Y && p.Y <= s.Y + s.Height).IsTrue();
            foreach (var offset in new[]
                     {
                         DragCoordinator.CancelButtonVerticalOffsetPhysical,
                         DragCoordinator.BackButtonVerticalOffsetPhysical,
                     })
            {
                Check.WithCustomMessage($"precondition: {where} is inside a drag exit zone.")
                     .That(p.DistanceTo(anchor + new PhysicalPoint(0, offset))).IsStrictlyGreaterThan(zone);
            }
            if (magnetEdgeExpected) return;
            var nearest = Math.Min(Math.Min(p.X - s.X, s.X + s.Width - p.X), Math.Min(p.Y - s.Y, s.Y + s.Height - p.Y));
            Check.WithCustomMessage($"precondition: {where} is within the edge magnet ({r:F0} px).")
                 .That(nearest).IsStrictlyGreaterThan(r);
        }

        /// <summary>fixup-02 precondition: the arming sample must be a saccade away from the rest.</summary>
        internal void RequireFarApart(string aName, PhysicalPoint a, string bName, PhysicalPoint b)
        {
            var leave = DragTrailViewModel.SameSpotRadii * Context.DragTrailVM.ReleaseRadiusPhysical;
            Check.WithCustomMessage(
                    $"precondition: {aName} and {bName} are only {a.DistanceTo(b):F0} px apart; the drop "
                    + $"arms only after the gaze leaves {leave:F0} px.")
                 .That(a.DistanceTo(b)).IsStrictlyGreaterThan(leave);
        }

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
