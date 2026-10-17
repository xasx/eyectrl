namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The two whole-pipeline pins for the action-menu modifier bar (spec round-4 F4 + round-5 F6),
/// built on the <c>EdgeButtonBarZOrderTests</c> / <c>ManualSelectionAnchorWiringTests</c> harness
/// pattern: every view model on ONE shared <see cref="WeakReferenceMessenger"/> plus a real pumped
/// <see cref="Dispatcher"/>, the real <see cref="OverlayMessageCoordinator"/> (show handler) AND
/// the real <see cref="OverlayVmBindingCoordinator"/> (the hoisted LatchedModifiers →
/// <c>ReplaceButton</c> handler), real view models for the action menu and BOTH edge bars, and a
/// real <c>PartialOverlayManager</c> supplying the mod bar, the action menu (the crossing path
/// hits <c>ShowActionMenu</c> through the RingVersion rebuild) and the glass backdrop.
///
/// <para>
/// <b>Held gaze toggles exactly once</b>: after a Shift activation the bar's re-arm gate feeds the
/// held samples as hits-nothing, so dwelling past the activation cannot re-toggle through the real
/// coordinator chain (gaze sample → <c>GazeCursorMovedMessage</c> → mod-bar
/// <c>DwellDetector.Update</c> → <c>ToggleActionMenuModifierMessage</c> → latch →
/// <c>ReplaceButton</c>).
/// </para>
///
/// <para>
/// <b>The masked mutation</b>: the LatchedModifiers handler must swap the icon with
/// <c>ReplaceButton</c>, NOT rebuild with <c>ShowButtons</c> — <c>ShowButtons</c> calls
/// <c>ResetCooldown</c>, and the toggle chain runs INSIDE the dwell activation that just armed the
/// detector's 400 ms cooldown. The timeline below separates the two: correct impl, the cooldown
/// gate blocks Ctrl accumulation START until T+400 ms, so Ctrl completes about T+1000; a
/// ShowButtons mutant lets Ctrl accumulate from first contact and fire about T+616. Centres are
/// DERIVED from the anchor the show handler actually passed plus the DpiHelper-scaled definition
/// offsets (Shift at minus-spacing, Ctrl at 0) — never hardcoded physical px, because CI runs
/// 96 DPI while the dev box runs 200 %.
/// </para>
///
/// <para>
/// Construction-only <c>[StaFact]</c>s — no app launch, no <c>Application</c> singleton, and the
/// class never writes <c>DpiHelper</c>, so it stays out of the exclusive DpiHelper collection.
/// </para>
/// </summary>
public class ActionMenuModBarMaskedMutationTests
{
    [StaFact]
    public void HeldGaze_TogglesExactlyOnce_ThroughTheRealCoordinator()
    {
        using var h = BuildFullHarness(); // real coordinator + real VMs; mod-bar VM on the shared messenger
        h.ShowActionMenu();                                // ShowActionMenuMessage + dispatcher pump
        for (var t = 0; t <= 6; t++)
            h.LookAt(h.ShiftCenter, t * 100_000);          // 100 ms cadence, dwell 600 ms: Shift fires
                                                           // at 600k (elapsed 600 ms at the 600k sample)
        for (var t = 7; t <= 30; t++)
            h.LookAt(h.ShiftCenter, t * 100_000);          // held: the gate feeds hits-nothing

        Check.That(h.ShiftToggleCount).IsEqualTo(1);

        // External review I1/M4: the toggle's USER-VISIBLE consequences — the latched icon on the
        // bar (via the real coordinator's ReplaceButton) and the latch itself. Without these, a
        // deleted swap handler or a net-zero double toggle left the suite green.
        Check.That(h.DefinitionOnBar(ActionMenuModifierButtons.ShiftId)!.Icon).IsEqualTo(ActionIcon.ShiftLatched);
        Check.That(h.DefinitionOnBar(ActionMenuModifierButtons.CtrlId)!.Icon).IsEqualTo(ActionIcon.Ctrl); // untouched: kills a wrong-id swap
        Check.That(h.ActionMenuVM.LatchedModifiers.Contains(ModifierKey.Shift)).IsTrue();

        // Leave for a REAL exit (span ≥ the 150 ms debounce on valid samples — 3.1M→3.26M is
        // 160 ms, and the point is 600 px right of Ctrl, outside every region), then re-enter and
        // dwell Shift out: the icon must return to the plain variant and the latch must empty.
        // Also kills an inverted latched-Contains in the swap handler.
        var exitPoint = new PhysicalPoint(h.CtrlCenter.X + 600, h.CtrlCenter.Y);
        h.LookAt(exitPoint, 3_100_000);
        h.LookAt(exitPoint, 3_260_000);                    // pending cleared: exit span 160 ms ≥ D
        for (var t = 34; t <= 40; t++)
            h.LookAt(h.ShiftCenter, t * 100_000);          // fresh dwell 3.4M..4.0M: toggles OFF at 4.0M

        Check.That(h.ShiftToggleCount).IsEqualTo(2);
        Check.That(h.DefinitionOnBar(ActionMenuModifierButtons.ShiftId)!.Icon).IsEqualTo(ActionIcon.Shift);
        Check.That(h.ActionMenuVM.LatchedModifiers).IsEmpty();
    }

    // Seven inside samples toggle Shift (100 ms cadence, fires at 600k):
    // correct impl - the cooldown gate blocks Ctrl accumulation START until T+400 ms, so
    //          Ctrl completes at about T+1000;
    // ShowButtons mutant - ResetCooldown means Ctrl accumulates from first contact and fires
    //          at about T+616.
    [StaFact]
    public void ShowButtonsMutant_IsCaught_AtT700ZeroFires_ThenCompletes()
    {
        using var h = BuildFullHarness();
        h.ShowActionMenu();
        for (var t = 0; t <= 6; t++)
            h.LookAt(h.ShiftCenter, t * 100_000);          // 100 ms cadence: Shift toggles at 600k = T
        // T+700 and T+1100 are TOGGLE-RELATIVE (T = 600k): correct fires at T+1000 = 1.6M;
        // the mutant fires at T+616 = 1.216M. Ctrl sampled densely from T+16k.
        for (var t = 616; t <= 1_300; t += 21)             // T+16k .. T+700k
            h.LookAt(h.CtrlCenter, t * 1_000);

        Check.That(h.CtrlToggleCount).IsEqualTo(0);        // the mutant fires by T+616 (1.216M)

        for (var t = 1_301; t <= 1_700; t += 25)           // T+701k .. T+1100k
            h.LookAt(h.CtrlCenter, t * 1_000);

        Check.That(h.CtrlToggleCount).IsEqualTo(1);        // correct impl fires at T+1000 (1.6M)
    }

    private static FullHarness BuildFullHarness() => new();

    /// <summary>
    /// Every VM on ONE shared messenger + a real (pumped) <see cref="Dispatcher"/>; the real
    /// message and vm-binding coordinators; real view models for the action menu and both bars;
    /// real settings; <see cref="TestFakes.MakeContext"/>'s <c>null!</c> VMs supplied (the
    /// vm-binding coordinator subscribes eleven VMs). The mod bar's
    /// <see cref="EdgeButtonBarViewModel"/> lives on the shared messenger — that channel is the
    /// thing under test.
    /// </summary>
    private sealed class FullHarness : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly OverlayMessageCoordinator _messages;
        private readonly OverlayVmBindingCoordinator _vmBinding;
        private readonly SharedMessengerBar _modBar;

        internal FullHarness()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var messenger = new WeakReferenceMessenger();
            Messenger = messenger;
            Settings = new EyeCtrlSettings();

            // BOTH bars real and on the ONE shared messenger: LookAt must reach the mod bar's
            // DwellDetector through the same channel production feeds it.
            _modBar = new SharedMessengerBar(Settings, messenger);
            var rightBar = new SharedMessengerBar(Settings, messenger);

            ActionMenuVM = new ActionMenuViewModel(Settings, messenger);

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                Messenger = messenger,
                Dispatcher = _dispatcher,
                Settings = Settings,
                InteractionManager = new InteractionManager(),
                ActionMenuVM = ActionMenuVM,
                CursorVM = new CursorCircleViewModel(Settings, messenger),
                ActivationVM = new ActivationButtonViewModel(messenger),
                ManualSelectionVM = new ManualSelectionOverlayViewModel(Settings, messenger),
                ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(messenger),
                ToolbarVM = new ToolbarViewModel(Settings, messenger),
                BackdropVM = new GlassBackdropViewModel(Settings, messenger),
                TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, Settings, messenger),
                ModesMenuVM = new ModesMenuViewModel(Settings, messenger),
                RepeatCursorVM = new RepeatCursorViewModel(messenger),
                TargetHighlightVM = new TargetHighlightOverlayViewModel(),
                OverlayManager = TestFakes.MakeOverlayManager(
                    edgeButtonBar: rightBar,
                    actionMenuModBar: _modBar,
                    actionMenu: new NoOpActionMenuOverlay(),
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay(),
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    activationButton: new TestFakes.FakeActivationButtonOverlay(),
                    manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                    manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay()),
            };

            var modal = new ModalInteractionState();
            _vmBinding = new OverlayVmBindingCoordinator(modal);
            _messages = new OverlayMessageCoordinator(
                new ToolbarLifecycleCoordinator(modal), _vmBinding);
            _messages.Initialize(context);
            _vmBinding.Initialize(context);

            messenger.Register<ToggleActionMenuModifierMessage>(this, (_, m) =>
            {
                if (m.Modifier == ModifierKey.Shift) ShiftToggleCount++;
                else if (m.Modifier == ModifierKey.Ctrl) CtrlToggleCount++;
            });
        }

        internal IMessenger Messenger { get; }

        internal EyeCtrlSettings Settings { get; }

        internal ActionMenuViewModel ActionMenuVM { get; }

        internal int ShiftToggleCount { get; private set; }

        internal int CtrlToggleCount { get; private set; }

        /// <summary>
        /// Physical centre of the Shift button: the anchor the show handler ACTUALLY passed, minus
        /// the DpiHelper-scaled spacing <c>ActionMenuModifierButtons.Definitions</c> uses. Derived,
        /// never hardcoded — the same arithmetic lands on different physical px at 96 and 200 %.
        /// </summary>
        internal PhysicalPoint ShiftCenter
        {
            get
            {
                var spacing = DpiHelper.LogicalToPhysicalY(
                    2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
                return ModAnchor + new PhysicalPoint(0, -spacing);
            }
        }

        /// <summary>Physical centre of the Ctrl button (offset 0 in the definitions).</summary>
        internal PhysicalPoint CtrlCenter => ModAnchor;

        /// <summary>The bar's CURRENT definition for a button id — the icon-swap observable (I1).</summary>
        internal EdgeButtonDefinition? DefinitionOnBar(string id) =>
            _modBar.EdgeBarVm?.DefinitionFor(id);

        private PhysicalPoint ModAnchor =>
            _modBar.LastAnchor
                ?? throw new InvalidOperationException("ShowActionMenu has not shown the mod bar yet");

        /// <summary>
        /// Opens the menu the way production does: a real <see cref="ShowActionMenuMessage"/> on the
        /// shared messenger, then a pump so the message coordinator's dispatched show handler runs
        /// and the mod bar exists before any gaze sample. The ring centre sits three-quarters across
        /// the physical screen so the ring's own dwell targets can never overlap the left-edge bar.
        /// </summary>
        internal void ShowActionMenu()
        {
            var screenW = System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX;
            var screenH = System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY;
            var center = new PhysicalPoint(screenW * 0.75, screenH * 0.5);
            Messenger.Send(new ShowActionMenuMessage(
                center,
                new List<InteractionAction> { InteractionAction.LeftClick },
                CircleSize: 250, DwellTimeMs: 600, GazePoint: center, ShowBack: false));
            Pump();
        }

        /// <summary>
        /// One raw gaze sample at tracker-clock <paramref name="timestampMicroseconds"/>. Delivery
        /// is synchronous (WeakReferenceMessenger), so the whole activation chain — detector,
        /// toggle message, latch, ReplaceButton — completes before this returns.
        /// </summary>
        internal void LookAt(PhysicalPoint position, long timestampMicroseconds) =>
            Messenger.Send(new GazeCursorMovedMessage(position, timestampMicroseconds));

        /// <summary>Runs everything queued at or above Background priority, draining the marshalled work.</summary>
        private void Pump() => _dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose()
        {
            _messages.Dispose();
            _vmBinding.Dispose();
        }
    }

    /// <summary>
    /// Wraps a caller-owned (SHARED-messenger) <see cref="EdgeButtonBarViewModel"/> behind
    /// <see cref="IEdgeButtonBar"/>, recording calls. <see cref="TestFakes.FakeEdgeButtonBar"/>
    /// constructs its view model on a private messenger, which would starve this bar of every
    /// <see cref="GazeCursorMovedMessage"/> the harness sends — the mod bar needs its own VM wired
    /// to the messenger the gaze samples ride.
    /// </summary>
    private sealed class SharedMessengerBar : IEdgeButtonBar
    {
        private readonly EdgeButtonBarViewModel _vm;

        internal SharedMessengerBar(EyeCtrlSettings settings, IMessenger messenger) =>
            _vm = new EdgeButtonBarViewModel(settings, messenger);

        public EdgeButtonBarViewModel? EdgeBarVm => _vm;

        /// <summary>The anchor of the last ShowButtons — the derivation source for the test centres.</summary>
        internal PhysicalPoint? LastAnchor { get; private set; }

        public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
        {
            LastAnchor = edgeAnchor;
            _vm.ShowButtons(definitions, edgeAnchor);
        }

        public void AddButton(EdgeButtonDefinition definition) => _vm.AddButton(definition);

        public void RemoveButton(string id) => _vm.RemoveButton(id);

        public void Hide() => _vm.Hide();
    }

    /// <summary>No-op ring window: satisfies the vm-binding coordinator's show/crossing paths.</summary>
    private sealed class NoOpActionMenuOverlay : IActionMenuOverlay
    {
        public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
            int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
            PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
            int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false, double previewMagnification = 1.0) { }

        public void Hide() { }
    }
}
