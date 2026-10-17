namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// G-A3 (the close half): every way out of the big magnifier now writes ONE line naming the exit —
/// <c>Magnifier closed by edge-close|backdrop-close|esc|commit|pause</c> — so a session that "didn't
/// magnify" can be placed on the timeline and its exit is never a mystery.
///
/// <para>
/// This drives the REAL <see cref="OverlayMessageCoordinator"/> and <see cref="OverlayVmBindingCoordinator"/>
/// pair over a real <see cref="InteractionManager"/> and the real magnifier view model, with recording
/// fakes for the windows. The InteractionManager wiring mirrors <c>GazeEngineAdapter</c>: starts are
/// synchronous, while End/Hide are posted to the dispatcher (as production posts them), which matters
/// for the Pause case — the state is Paused by the time the hide lands, and that is what attributes it.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionCloseReasonTests
{
    [StaFact]
    public void EdgeClose_LogsTheEdgeCloseReason()
    {
        using var harness = Harness.Start();
        harness.Messenger.Send(new ManualSelectionCloseMessage());
        harness.Pump();

        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.EdgeClose);
    }

    [StaFact]
    public void BackdropClose_LogsTheBackdropCloseReason()
    {
        using var harness = Harness.Start();
        harness.Messenger.Send(new BackdropDismissMessage());
        harness.Pump();

        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.BackdropClose);
    }

    [StaFact]
    public void Escape_LogsTheEscReason()
    {
        using var harness = Harness.Start();
        harness.Hatch.Press();
        harness.Pump();

        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.Esc);
    }

    [StaFact]
    public void Commit_LogsTheCommitReason()
    {
        using var harness = Harness.Start();
        harness.Messenger.Send(new ManualSelectionDwellCompletedMessage(new PhysicalPoint(950, 560)));
        harness.Pump();

        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.Commit);
    }

    [StaFact]
    public void Pause_LogsThePauseReason()
    {
        using var harness = Harness.Start();
        harness.InteractionManager.TogglePause();
        harness.Pump();

        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.Pause);
    }

    [StaFact]
    public void ASecondSession_DoesNotInheritThePreviousCloseReason()
    {
        using var harness = Harness.Start();
        harness.Messenger.Send(new ManualSelectionCloseMessage());
        harness.Pump();
        harness.CheckExactlyOneClosedLine(MagnifierCloseReasons.EdgeClose);

        // F4: a new session must forget the previous exit's attribution. Without the reset the
        // stale "edge-close" wins on the ?? and the pause inference never runs.
        harness.StartSecondSession();
        harness.InteractionManager.TogglePause();
        harness.Pump();

        var lines = harness.ClosedLines();
        Check.WithCustomMessage($"the second session's exit was misattributed: {string.Join(" | ", lines)}")
            .That(lines).HasSize(2);
        Check.That(lines[1]).StartsWith("Magnifier closed by pause after ");
    }

    private sealed class Harness : IDisposable
    {
        private readonly OverlayMessageCoordinator _messageCoordinator;
        private readonly OverlayVmBindingCoordinator _vmBindingCoordinator;
        private readonly ILogger _previousLogger;

        internal Dispatcher Dispatcher { get; }
        internal WeakReferenceMessenger Messenger { get; } = new();
        internal InteractionManager InteractionManager { get; }
        internal TestFakes.FakeEscapeHatch Hatch { get; }
        internal List<string> Messages { get; } = new();

        private Harness()
        {
            Dispatcher = Dispatcher.CurrentDispatcher;
            InteractionManager = new InteractionManager();
            Hatch = new TestFakes.FakeEscapeHatch();

            var sink = new CapturingSink(Messages);
            _previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

            var settings = new EyeCtrlSettings();
            var manualSelectionVm = new ManualSelectionOverlayViewModel(settings, Messenger);
            var modal = new ModalInteractionState();
            _messageCoordinator = new OverlayMessageCoordinator(
                new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
            _vmBindingCoordinator = new OverlayVmBindingCoordinator(modal);

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                InteractionManager = InteractionManager,
                Messenger = Messenger,
                Dispatcher = Dispatcher,
                Settings = settings,
                ManualSelectionVM = manualSelectionVm,
                ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(Messenger),
                CursorVM = new CursorCircleViewModel(settings, Messenger),
                ActivationVM = new ActivationButtonViewModel(Messenger),
                ActionMenuVM = new ActionMenuViewModel(settings, Messenger),
                ToolbarVM = new ToolbarViewModel(settings, Messenger),
                BackdropVM = new GlassBackdropViewModel(settings, Messenger),
                TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, settings, Messenger),
                // The BackdropDismiss handler reads this VM unconditionally.
                TargetHighlightVM = new TargetHighlightOverlayViewModel(),
                EscapeHatch = Hatch,
                OverlayManager = TestFakes.MakeOverlayManager(
                    manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                    manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    activationButton: new TestFakes.FakeActivationButtonOverlay(),
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay(),
                    edgeButtonBar: new TestFakes.FakeEdgeButtonBar()),
            };

            _messageCoordinator.Initialize(context);
            _vmBindingCoordinator.Initialize(context);

            // The production adapter's thread marshaling: Start is synchronous, End/Hide are posted.
            InteractionManager.StartManualSelection += point => Messenger.Send(
                new StartManualSelectionMessage(new PhysicalPoint(960, 540), 170, 2.0, point));
            InteractionManager.RestoreManualSelectionView += point => Messenger.Send(
                new StartManualSelectionMessage(new PhysicalPoint(960, 540), 170, 2.0, point, Recapture: false));
            InteractionManager.EndManualSelection += () =>
                Dispatcher.BeginInvoke(() => Messenger.Send(new EndManualSelectionMessage()));
            InteractionManager.HideManualSelection += () =>
                Dispatcher.BeginInvoke(() => Messenger.Send(new ManualSelectionHiddenMessage()));
        }

        internal static Harness Start()
        {
            var harness = new Harness();
            harness.InteractionManager.SelectionMode = SelectionMode.ManualSelection;
            harness.InteractionManager.OnFixationStarted(new PhysicalPoint(950, 560));
            harness.InteractionManager.OnActivationButtonDwelled();
            harness.Pump();
            return harness;
        }

        internal void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        /// <summary>Starts a second manual-selection session after the first one exited.</summary>
        internal void StartSecondSession()
        {
            InteractionManager.OnFixationStarted(new PhysicalPoint(950, 560));
            InteractionManager.OnActivationButtonDwelled();
            Pump();
        }

        internal List<string> ClosedLines() =>
            Messages.Where(m => m.StartsWith("Magnifier closed by ", StringComparison.Ordinal)).ToList();

        internal void CheckExactlyOneClosedLine(string expectedReason)
        {
            var lines = Messages
                .Where(m => m.StartsWith("Magnifier closed by ", StringComparison.Ordinal))
                .ToList();
            Check.WithCustomMessage(
                    $"expected exactly one 'Magnifier closed by {expectedReason}' line; saw: "
                    + string.Join(" | ", Messages))
                .That(lines).HasSize(1);
            Check.WithCustomMessage($"wrong exit attribution: {lines[0]}")
                .That(lines[0]).StartsWith($"Magnifier closed by {expectedReason} after ");
        }

        public void Dispose()
        {
            _vmBindingCoordinator.Dispose();
            _messageCoordinator.Dispose();
            Log.Logger = _previousLogger;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<string> _messages;
        public CapturingSink(List<string> messages) => _messages = messages;
        public void Emit(LogEvent logEvent) => _messages.Add(logEvent.RenderMessage());
    }
}
