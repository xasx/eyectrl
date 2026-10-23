namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// App-plumbing pins for the LEFT-edge action-menu modifier bar: the show handler in
/// <see cref="OverlayMessageCoordinator"/> builds <c>ActionMenuModifierButtons.Definitions</c> at
/// <c>ScreenGeometry.LeftEdgeAnchor</c>, and the hide handler hides the mod bar through the
/// ownership API. Unlike the shared right-edge bar, where <c>Hide(ActionMenu)</c> must leave a
/// displacing context's buttons alone, nothing but the action menu ever owns the left bar. Both
/// sides isolate a throwing mod bar in its own try, so an overlay manager without the slot (the
/// default-throw interface member, or <c>PartialOverlayManager</c> with no slot supplied) cannot
/// cost the user the right-bar buttons or the TargetHighlight hide.
///
/// <para>
/// Built on the <c>HideActionMenuBarOwnershipTests</c> harness shape: the real state machine, the
/// real <c>GazeEngineAdapter</c> (so the dispatcher marshalling that creates the hide/show ordering
/// is production's), the real <c>DragCoordinator</c> and the real
/// <c>OverlayMessageCoordinator</c>, over recording bars. The harness supplies a REAL
/// <c>ActionMenuViewModel</c> — the show handler reads <c>Context.ActionMenuVM.LatchedModifiers</c>
/// inside its try, and the shared builder leaves that VM null (<c>TestFakes.MakeContext</c>), where
/// a null would NRE, be caught by the mod bar's own try, and leave the bar never shown.
/// </para>
///
/// <para>
/// <c>[StaFact]</c> for the same reason as the ownership tests: the harness pumps a real
/// <see cref="Dispatcher"/>. The drag's trail overlay is the recording fake through the
/// coordinator's <c>CreateTrailOverlay</c> seam, so no window is constructed and nothing is shown
/// on the machine running the suite.
/// </para>
/// </summary>
public class ActionMenuModBarWiringTests
{
    [StaFact]
    public void ShowActionMenu_ShowsTheModBar_EvenWithoutAResolvedControl()
    {
        using var h = BuildContext();
        h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
        h.PumpDispatcher();

        Check.That(h.ModBarCalls).Contains("ShowButtons");
    }

    [StaFact]
    public void HideActionMenu_HidesTheModBar_EvenWhenTheRightBarIsOwnedByADrag()
    {
        using var h = BuildContext();
        // Reach Dragging the way the ownership tests do: fixation, then activation dwell (menu
        // shown), then select Drag. OnActionSelected(Drag) from FreeLook would be a no-op.
        h.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        h.InteractionManager.OnActivationButtonDwelled();
        h.PumpDispatcher();
        h.InteractionManager.OnActionSelected(InteractionAction.Drag); // DragStarted: drag owns the right bar
        h.PumpDispatcher();
        h.Messenger.Send(new HideActionMenuMessage());
        h.PumpDispatcher();

        Check.That(h.ModBarCalls).Contains("Hide");
        // The visible state, not just the call: a release that names the wrong owner records a
        // "Hide" on this fake and leaves the real view model showing (M23).
        Check.That(h.ModBar).IsNotNull();
        Check.WithCustomMessage(
                $"the mod bar still shows [{string.Join(",", h.ModBar!.ShownButtonIds)}]")
             .That(h.ModBar.EdgeBarVm!.IsVisible).IsFalse();
        Check.That(h.ModBar.EdgeBarVm.Buttons).IsEmpty();
        // The right bar's ownership release is a no-op on screen at this point: the menu is
        // displaced, so Hide(ActionMenu) only withdraws its saved claim and the drag's cancel —
        // the user's only way out — stays put.
        Check.WithCustomMessage(
                $"right bar holds [{string.Join(",", h.RightBar.ShownButtonIds)}] "
                + $"after calls [{string.Join(",", h.RightBarCalls)}]")
             .That(h.RightBar.ShownButtonIds).ContainsExactly("Drag_cancel");
    }

    [StaFact]
    public void ThrowingModBar_DoesNotPreventTheRightBarHide()
    {
        using var h = BuildContext(withModBar: false); // mod-bar slot absent ⇒ the left-bar hide throws;
        // TargetHighlightVM.IsVisible = true (the TargetHighlight hide is the casualty under test)
        h.TargetHighlightVM.IsVisible = true;
        h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
        h.PumpDispatcher();
        h.Messenger.Send(new HideActionMenuMessage());
        h.PumpDispatcher();

        Check.That(h.RightBarCalls).Contains("Hide"); // the right-bar hide ran before the throw
        Check.That(h.TargetHighlightVM.IsVisible).IsFalse(); // the TargetHighlight hide also ran
    }

    [StaFact]
    public void ThrowingModBar_DoesNotPreventTheRightBarShow_OrTheTargetHighlightShow()
    {
        using var h = BuildContext(withModBar: false); // PartialOverlayManager WITHOUT the slot ⇒ default throws
        // A resolved control turns ON the TargetHighlight show that FOLLOWS the mod-bar block in
        // the same outer try — the code the inner try actually protects (external review M1: the
        // right bar is shown BEFORE the mod-bar block, so a removed inner catch could never cost
        // it; what it would skip is this). IsVisible is set BEFORE the throwing overlay stub, so
        // the VM flag is the observable.
        h.Messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick },
            CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false)
        {
            ResolvedControl = new ControlInfo(new PhysicalRect(100, 100, 50, 50)),
        });
        h.PumpDispatcher();

        Check.That(h.RightBarCalls).Contains("ShowButtons"); // shown before the mod-bar block runs
        Check.That(h.TargetHighlightVM.IsVisible).IsTrue();  // the inner try kept the fault off this path
    }

    private static Harness BuildContext(bool withModBar = true) => new(withModBar);

    /// <summary>
    /// The <c>HideActionMenuBarOwnershipTests</c> harness plus the mod-bar slots: the real state
    /// machine, adapter, drag coordinator and message coordinator over a recording right bar and a
    /// recording mod bar, plus the real <c>ActionMenuViewModel</c> the show handler reads the latch
    /// from. <c>withModBar: false</c> leaves the mod-bar slot unsupplied so
    /// <c>PartialOverlayManager.ActionMenuModBar</c> throws — the default-throw interface member's
    /// in-test stand-in.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly GazeEngine _engine;
        private readonly GazeEngineAdapter _adapter;
        private readonly DragCoordinator _drag;
        private readonly OverlayMessageCoordinator _messages;
        private readonly Dispatcher _dispatcher;
        private readonly TestFakes.FakeEdgeButtonBar? _modBar;

        internal Harness(bool withModBar)
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

            RightBar = new TestFakes.FakeEdgeButtonBar();
            _modBar = withModBar ? new TestFakes.FakeEdgeButtonBar() : null;

            TargetHighlightVM = new TargetHighlightOverlayViewModel();

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                InteractionManager = InteractionManager,
                GazeEngine = _engine,
                Messenger = messenger,
                Dispatcher = _dispatcher,
                Settings = settings,
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                DragTrailVM = new DragTrailViewModel(settings, messenger),
                TargetHighlightVM = TargetHighlightVM,
                OverlayManager = TestFakes.MakeOverlayManager(
                    edgeButtonBar: RightBar,
                    actionMenuModBar: _modBar,
                    manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                    manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                    // The drag's OnDragStarted completes the backdrop's pending hide before the
                    // press (the deferred-hide fix); the manager must carry one or the property
                    // throws by design.
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            };

            _drag = new DragCoordinator();
            _drag.CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay();
            _drag.Initialize(context);

            // The vm-binding sibling is constructed but deliberately NOT initialized (the
            // ownership-harness pattern): these tests never drive the LatchedModifiers path, and
            // initializing it would subscribe overlay handlers this harness has no windows for.
            var modal = new ModalInteractionState();
            _messages = new OverlayMessageCoordinator(
                new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
            _messages.Initialize(context);
        }

        internal InteractionManager InteractionManager { get; }

        internal TestFakes.FakeEdgeButtonBar RightBar { get; }

        internal IReadOnlyList<string> ModBarCalls
        {
            get
            {
                if (_modBar is null) return Array.Empty<string>();
                return _modBar.Calls;
            }
        }

        /// <summary>The mod bar itself, so a test can assert its VISIBLE state (not just calls).</summary>
        internal TestFakes.FakeEdgeButtonBar? ModBar => _modBar;

        internal IReadOnlyList<string> RightBarCalls => RightBar.Calls;

        internal TargetHighlightOverlayViewModel TargetHighlightVM { get; }

        /// <summary>
        /// The messenger the adapter and coordinators share — exposed so a test can observe the
        /// messages the real pipeline sends (the adapter marshals via BeginInvoke, so pump after
        /// every step).
        /// </summary>
        internal IMessenger Messenger { get; }

        /// <summary>
        /// Drains the dispatcher queue. A Background-priority Invoke waits out every Normal-priority
        /// item INCLUDING ones queued while it drains, which is what the doubly-deferred hide needs:
        /// the adapter's BeginInvoke sends the message, and the handler queues another.
        /// </summary>
        internal void PumpDispatcher() => _dispatcher.Invoke(() => { }, DispatcherPriority.Background);

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
