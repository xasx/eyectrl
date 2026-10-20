namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The right-edge bar is shared, and <c>HideActionMenuMessage</c> is the one message that clears it
/// on behalf of a context that may no longer own it. <c>OverlayMessageCoordinator</c> guards that
/// with an ownership check —
/// <c>bar?.EdgeBarVm?.DefinitionFor(ActionMenuEdgeButtons.CloseId) is not null</c> — and the guard
/// is load-bearing in the strongest sense this app has: unguarded, it clears the drag's cancel
/// button the instant a drag begins, leaving a user whose only input channel is an eye tracker
/// holding a pressed mouse button with nothing on screen to release it.
///
/// <para>
/// <b>Why it had never been exercised.</b> <c>TestFakes.FakeEdgeButtonBar.EdgeBarVm</c> returned
/// <c>null</c> until 2026-09-16. A null view model does not make the guard neutral — it makes the
/// condition answer "no" unconditionally, so <c>bar.Hide()</c> was unreachable from every test in
/// this suite and both arms of the guard were invisible. Deleting the guard, or deleting the
/// <c>Hide()</c> it protects, left the whole suite green.
/// </para>
///
/// <para>
/// <b>Why these tests drive the real pipeline rather than posting the message directly.</b> The
/// guard's premise is an ORDERING claim, spelled out in its own comment: the hide is deferred (the
/// adapter marshals it, and the handler defers again), while the contexts that take the bar over
/// are not — <c>DragCoordinator.OnDragStarted</c> runs synchronously off
/// <c>InteractionManager.DragStarted</c> and has already called <c>ShowButtons</c> by the time the
/// hide lands. A test that set the bar up by hand and then sent the message would check the
/// guard's arithmetic while assuming away the thing that makes it necessary. So these run the real
/// <c>GazeEngineAdapter</c>, the real <c>InteractionManager</c> and the real
/// <c>DragCoordinator</c>, and let the dispatcher decide the order.
/// </para>
///
/// <para>
/// <c>[StaFact]</c> because the harness drives a real <see cref="Dispatcher"/>, which these tests
/// pump to let production's marshalling decide the ordering. It no longer needs an STA thread for
/// the drag's own overlay: the coordinator's trail factory is pointed at a recording fake
/// (<see cref="TestFakes.FakeDragTrailOverlay"/>), so no window is constructed and nothing appears
/// on the machine running the suite. That is the F2 seam — before it, a successful drag built a
/// real maximized, topmost <c>DragTrailOverlay</c> here, which is what made this class unsafe to
/// run locally and dependent on the CI desktop.
/// </para>
/// </summary>
public class HideActionMenuBarOwnershipTests
{
    [StaFact]
    public void HideActionMenu_LeavesTheDragsCancelButtonAlone_WhenTheDragOwnsTheBar()
    {
        using var h = new Harness();

        // Open the plain action menu. SelectionMode.None is the default, which is the branch that
        // reaches ActionMenuVisible; the menu's own Close/Help land on the shared bar from
        // OverlayMessageCoordinator's ShowActionMenuMessage handler.
        h.InteractionManager.OnFixationStarted(new PhysicalPoint(400, 300));
        h.InteractionManager.OnActivationButtonDwelled();
        h.Pump();

        Check.That(h.InteractionManager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.That(h.Bar.ShownButtonIds)
             .ContainsExactly(ActionMenuEdgeButtons.CloseId, ActionMenuEdgeButtons.HelpId);

        // Choosing Drag fires HideActionMenu and then DragStarted. The first is marshalled, the
        // second is not — so the cancel button is on the bar before the hide is even sent.
        h.InteractionManager.OnActionSelected(InteractionAction.Drag);

        // The flake this ticket exists for: a throw anywhere in the drag's setup aborts the drag to
        // FreeLook before the assertions below, and the exception used to go only to a logger the
        // test process never configures. Asserting null first (with the exception in the message)
        // turns the next occurrence from "state was FreeLook" into a named cause.
        Check.WithCustomMessage(h.Drag.LastSetupFailure?.ToString() ?? "")
             .That(h.Drag.LastSetupFailure).IsNull();

        // The seam was actually used: the recording fake was configured and shown, and no real
        // overlay was constructed (which would have no place in this count).
        Check.That(h.TrailOverlay.ConfigureCount).IsEqualTo(1);
        Check.That(h.TrailOverlay.ShowCount).IsEqualTo(1);

        Check.That(h.InteractionManager.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(h.Bar.ShownButtonIds).ContainsExactly("Drag_cancel");

        h.Pump();

        // The guard's whole purpose. Without it the deferred hide arrives after the drag has taken
        // the bar and clears it, and the drag has no visible way out.
        Check.WithCustomMessage(
                $"bar holds [{string.Join(",", h.Bar.ShownButtonIds)}] after calls [{string.Join(",", h.Bar.Calls)}]")
             .That(h.Bar.ShownButtonIds).ContainsExactly("Drag_cancel");

        // Asserted through the view model as well, because that is what the guard actually reads.
        // ShownButtonIds is the fake's own bookkeeping and would survive a guard that consulted a
        // stale or empty view model — or a fake that went back to handing out null, which is how
        // this whole path stayed untested. Checked separately so that regression reports itself by
        // name instead of as a NullReferenceException at a line number.
        Check.WithCustomMessage(
                "FakeEdgeButtonBar.EdgeBarVm is null — the ownership guard cannot be reached, "
                + "so this test would be asserting nothing about it")
             .That(h.Bar.EdgeBarVm).IsNotNull();
        Check.That(h.Bar.EdgeBarVm!.DefinitionFor("Drag_cancel")).IsNotNull();
    }

    [StaFact]
    public void HideActionMenu_ClearsTheBar_WhenTheMenuStillOwnsIt()
    {
        using var h = new Harness();

        h.InteractionManager.OnFixationStarted(new PhysicalPoint(400, 300));
        h.InteractionManager.OnActivationButtonDwelled();
        h.Pump();

        Check.That(h.Bar.ShownButtonIds)
             .ContainsExactly(ActionMenuEdgeButtons.CloseId, ActionMenuEdgeButtons.HelpId);

        // Dismissing the menu with nothing taking the bar over — the ordinary case, and the arm
        // that fails if someone "fixes" the guard by deleting the Hide() rather than the condition.
        h.InteractionManager.OnActionMenuDismissed();
        h.Pump();

        Check.WithCustomMessage(
                $"bar holds [{string.Join(",", h.Bar.ShownButtonIds)}] after calls [{string.Join(",", h.Bar.Calls)}]")
             .That(h.Bar.ShownButtonIds).IsEmpty();
        Check.That(h.Bar.Calls).Contains("Hide");
        Check.That(h.Bar.EdgeBarVm!.DefinitionFor(ActionMenuEdgeButtons.CloseId)).IsNull();
    }

    // The modifiers must survive the full ActionSelectedMessage → InteractionManager →
    // ActionRequestedMessage hop (GazeEngineAdapter :108/:335): dropping them at either forward
    // severs the only path the menu's modifier latch has to the input simulator. The ownership
    // Harness wires a REAL GazeEngineAdapter over the real state machine, so this exercises
    // production's forwarding, not a hand-posted message.
    [StaFact]
    public void ActionSelectedMessage_Modifiers_SurviveToActionRequestedMessage()
    {
        using var h = new Harness();
        var received = new List<ActionRequestedMessage>();
        h.Messenger.Register<ActionRequestedMessage>(h, (_, m) => received.Add(m));

        // Reach the menu state: FreeLook OnActionSelected returns without raising.
        h.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        h.InteractionManager.OnActivationButtonDwelled();
        h.Pump();

        h.Messenger.Send(new ActionSelectedMessage(InteractionAction.LeftClick, new HashSet<ModifierKey> { ModifierKey.Shift }));
        h.Pump();

        Check.That(received.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue(); // kills :107/:334
    }

    /// <summary>
    /// The real state machine, the real adapter (so the marshalling that creates the ordering is
    /// production's), the real <see cref="DragCoordinator"/> and the real
    /// <see cref="OverlayMessageCoordinator"/>, over a recording edge bar. Only the overlays the
    /// two coordinators dereference are supplied; the rest of
    /// <see cref="TestFakes.MakeOverlayManager"/> throws, so a coordinator reaching further fails
    /// loudly.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly GazeEngine _engine;
        private readonly GazeEngineAdapter _adapter;
        private readonly DragCoordinator _drag;
        private readonly OverlayMessageCoordinator _messages;
        private readonly Dispatcher _dispatcher;

        internal Harness()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var messenger = new WeakReferenceMessenger();
            Messenger = messenger;
            var settings = new EyeCtrlSettings();

            _engine = new GazeEngine(
                new TestFakes.FakeGazeProvider(), new TestFakes.FakeInputSimulator(), settings);
            InteractionManager = _engine.InteractionManager;

            _adapter = new GazeEngineAdapter(
                _engine, settings, messenger, _dispatcher,
                NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

            Bar = new TestFakes.FakeEdgeButtonBar();

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                InteractionManager = InteractionManager,
                GazeEngine = _engine,
                Messenger = messenger,
                Dispatcher = _dispatcher,
                Settings = settings,
                DragTrailVM = new DragTrailViewModel(settings, messenger),
                TargetHighlightVM = new TargetHighlightOverlayViewModel(),
                OverlayManager = TestFakes.MakeOverlayManager(
                    edgeButtonBar: Bar,
                    manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                    manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                    // HandleActionRequested completes the backdrop's pending hide before injection
                    // (the deferred-hide fix); the manager must carry one or the property throws.
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            };

            _drag = new DragCoordinator();
            TrailOverlay = new TestFakes.FakeDragTrailOverlay();
            _drag.CreateTrailOverlay = () => TrailOverlay;
            _drag.Initialize(context);

            // The vm-binding sibling is constructed but deliberately NOT initialized: the message
            // coordinator only reaches into it for HideBackdrop (the BackdropDismissMessage path),
            // which these tests never take, and initializing it would subscribe overlay handlers
            // this harness has no windows for.
            var modal = new ModalInteractionState();
            _messages = new OverlayMessageCoordinator(
                new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
            _messages.Initialize(context);
        }

        internal InteractionManager InteractionManager { get; }

        internal TestFakes.FakeEdgeButtonBar Bar { get; }

        /// <summary>The drag's trail overlay fake — no window, and it counts Configure/Show.</summary>
        internal TestFakes.FakeDragTrailOverlay TrailOverlay { get; }

        /// <summary>The drag coordinator under test, exposed so a test can read its diagnostics.</summary>
        internal DragCoordinator Drag => _drag;

        /// <summary>
        /// The messenger the adapter and coordinators share — exposed so a test can observe the
        /// messages the real pipeline sends (the adapter marshals via BeginInvoke, so pump after
        /// every step).
        /// </summary>
        public IMessenger Messenger { get; }

        /// <summary>
        /// Drains the dispatcher queue. A Background-priority Invoke waits out every Normal-priority
        /// item INCLUDING ones queued while it drains, which is what the doubly-deferred hide needs:
        /// the adapter's BeginInvoke sends the message, and the handler queues another.
        /// </summary>
        internal void Pump() => _dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose()
        {
            _messages.Dispose();
            _drag.Dispose();
            _adapter.Dispose();
            _engine.Dispose();
        }
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }
}
