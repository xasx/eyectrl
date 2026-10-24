namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The shared right-edge bar tracks who shows it. These pin the safety properties the ownership
/// API exists for: a hide by a caller that does not own the bar changes nothing; a displaced
/// owner's full set (buttons AND anchor) comes back when the displacer leaves; a context that
/// releases while displaced only withdraws its saved claim, so a closed context can never be
/// resurrected.
///
/// <para>
/// The headline journey is the ticket's: keyboard → repeat → exit leaves the keyboard's Close and
/// Flip on screen again. It runs the real <see cref="RepeatModeController"/> over a real
/// <see cref="InteractionManager"/> and the recording bar, whose view model is real — so the claim
/// stack, the state transition and the production definition placement are all exercised.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class EdgeBarOwnershipTests
{
    private static readonly PhysicalPoint KeyboardAnchor = new(1800, 300);
    private static readonly PhysicalPoint SharedAnchor = new(1800, 800);

    public EdgeBarOwnershipTests()
    {
        // EdgeButtonBarViewModel converts each definition's radius through DpiHelper.
        DpiHelper.Initialize(1.0, 1.0);
    }

    private static EdgeButtonDefinition Def(string id, double verticalOffset = 0) =>
        new(id, Symbol: string.Empty, 600, _ => { }, verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius);

    private static EdgeButtonBarViewModel NewBar() =>
        new(new EyeCtrlSettings(), new WeakReferenceMessenger());

    [Fact]
    public void HideByTheOwner_ClearsTheBar()
    {
        var bar = NewBar();
        bar.ShowButtons([Def("keyboard_close")], KeyboardAnchor, EdgeBarOwner.Keyboard);
        Check.That(bar.CurrentOwner).IsEqualTo(EdgeBarOwner.Keyboard);

        bar.Hide(EdgeBarOwner.Keyboard);

        Check.That(bar.CurrentOwner).IsNull();
        Check.That(bar.Buttons).IsEmpty();
        Check.That(bar.IsVisible).IsFalse();
    }

    [Fact]
    public void HideByANonOwner_IsANoOp()
    {
        // A deferred hide can arrive from a context that never owned this bar (or has already
        // released it). It must not clear whatever is on screen — that surface may be the user's
        // only way out.
        var bar = NewBar();
        bar.ShowButtons([Def("keyboard_close")], KeyboardAnchor, EdgeBarOwner.Keyboard);

        bar.Hide(EdgeBarOwner.Drag);

        Check.That(bar.CurrentOwner).IsEqualTo(EdgeBarOwner.Keyboard);
        Check.That(bar.Buttons.Select(b => b.Id)).ContainsExactly("keyboard_close");
        Check.That(bar.IsVisible).IsTrue();
    }

    [Fact]
    public void ADisplacingOwner_IsRestored_WhenTheDisplacerReleases()
    {
        // The displaced set is saved whole: definitions AND anchor are put back, because the
        // keyboard's pair lives on its own tracked anchor, not the shared centre one.
        var bar = NewBar();
        bar.ShowButtons(
            [Def("keyboard_close"), Def("keyboard_flip", 60)],
            KeyboardAnchor,
            EdgeBarOwner.Keyboard);

        bar.ShowButtons([Def("repeat-stop")], SharedAnchor, EdgeBarOwner.Repeat);
        Check.That(bar.Buttons.Select(b => b.Id)).ContainsExactly("repeat-stop");

        bar.Hide(EdgeBarOwner.Repeat);

        Check.That(bar.CurrentOwner).IsEqualTo(EdgeBarOwner.Keyboard);
        Check.That(bar.Buttons.Select(b => b.Id))
             .ContainsExactly("keyboard_close", "keyboard_flip");
        Check.That(bar.SeedCenters["keyboard_close"]).IsEqualTo(KeyboardAnchor);
        Check.That(bar.IsVisible).IsTrue();
    }

    [Fact]
    public void AReleaseByADisplacedOwner_DoesNotTearDownItsSuccessor()
    {
        // The deferred menu-dismiss shape: the menu claims, a drag takes the bar, then the menu's
        // queued Hide lands. It must not clear the drag's exit, and it must withdraw the menu's
        // saved claim — otherwise the menu would be resurrected when the drag ends.
        var bar = NewBar();
        bar.ShowButtons([Def("ActionMenu_close")], SharedAnchor, EdgeBarOwner.ActionMenu);
        bar.ShowButtons([Def("Drag_cancel")], SharedAnchor, EdgeBarOwner.Drag);

        bar.Hide(EdgeBarOwner.ActionMenu);

        Check.That(bar.Buttons.Select(b => b.Id)).ContainsExactly("Drag_cancel");

        bar.Hide(EdgeBarOwner.Drag);

        Check.That(bar.Buttons).IsEmpty();
        Check.That(bar.IsVisible).IsFalse();
    }

    [Fact]
    public void ASecondShowByTheSameOwner_ReplacesItsClaim_InsteadOfStacking()
    {
        // The keyboard's flip rebuild shows without releasing first. A stacked duplicate would be
        // restored after the real release, leaving a stale pair behind.
        var bar = NewBar();
        bar.ShowButtons([Def("keyboard_close")], KeyboardAnchor, EdgeBarOwner.Keyboard);
        bar.ShowButtons(
            [Def("keyboard_close"), Def("keyboard_flip", 60)],
            SharedAnchor,
            EdgeBarOwner.Keyboard);

        bar.Hide(EdgeBarOwner.Keyboard);

        Check.That(bar.Buttons).IsEmpty();
        Check.That(bar.CurrentOwner).IsNull();
    }

    [Fact]
    public void ButtonsAddedAfterTheClaim_AreRestoredWithTheOwner()
    {
        // A drag adds its Back button after claiming the bar; if the drag is displaced and
        // restored, the saved set must carry that addition.
        var bar = NewBar();
        bar.ShowButtons([Def("Drag_cancel", -60)], SharedAnchor, EdgeBarOwner.Drag);
        bar.AddButton(Def("Drag_back", 60));

        bar.ShowButtons([Def("ManualSelection_close")], SharedAnchor, EdgeBarOwner.ManualSelection);
        bar.Hide(EdgeBarOwner.ManualSelection);

        Check.That(bar.Buttons.Select(b => b.Id))
             .ContainsExactly("Drag_cancel", "Drag_back");
    }

    [Fact]
    public void DragCancel_RestoresTheOwnerItDisplaced()
    {
        // The AC's drag leg, through the real coordinator (the fake bar delegates to a real view
        // model): the keyboard pair is up, the drag takes the bar, Cancel puts the pair back.
        var input = new TestFakes.FakeInputSimulator();
        var (context, bar) = TestFakes.MakeDragContextWithBar(input);
        bar.ShowButtons(
            KeyboardEdgeButtons.Definitions(600), KeyboardAnchor, EdgeBarOwner.Keyboard);

        var drag = new DragCoordinator
        {
            CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay(),
        };
        drag.Initialize(context);

        drag.OnDragStarted(new PhysicalPoint(100, 100));
        Check.That(bar.ShownButtonIds).ContainsExactly("Drag_cancel");
        Check.WithCustomMessage("the drag drew its Cancel off the shared exit anchor")
             .That(bar.EdgeBarVm!.SeedCenters["Drag_cancel"]).IsEqualTo(
                 DragCoordinator.ExitAnchorPhysical
                 + new PhysicalPoint(0, DragCoordinator.CancelButtonVerticalOffsetPhysical));

        drag.OnDragCancel();

        Check.WithCustomMessage($"bar holds [{string.Join(",", bar.ShownButtonIds)}]")
             .That(bar.ShownButtonIds)
             .ContainsExactly(KeyboardEdgeButtons.CloseId, KeyboardEdgeButtons.FlipId);
    }

    [StaFact]
    public void ScrollStop_DisplacesTheKeyboard_AndRestoresItOnExit()
    {
        // Scroll's calls were migrated with the scope extension: its stop claims the bar, and its
        // exit restores whoever it displaced rather than blanking the shared surface.
        var bar = new TestFakes.FakeEdgeButtonBar();
        var (controller, im, _, _) =
            ScrollModeControllerThreadingTests.NewScrollWorld(
                new TestFakes.FakeInputSimulator(), edgeBar: bar);
        using (controller)
        {
            bar.ShowButtons(
                KeyboardEdgeButtons.Definitions(600), KeyboardAnchor, EdgeBarOwner.Keyboard);

            ScrollModeControllerThreadingTests.EnterScrollMode(im);
            Check.That(bar.ShownButtonIds).ContainsExactly("scroll-stop");

            im.ExitScrollMode();

            Check.WithCustomMessage($"bar holds [{string.Join(",", bar.ShownButtonIds)}]")
                 .That(bar.ShownButtonIds)
                 .ContainsExactly(KeyboardEdgeButtons.CloseId, KeyboardEdgeButtons.FlipId);
        }
    }

    [StaFact]
    public void KeyboardToRepeatToExit_RestoresCloseAndFlip()
    {
        // The ticket's headline journey, through the real controller and state machine.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var bar = new TestFakes.FakeEdgeButtonBar();
        var im = new InteractionManager();
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(edgeButtonBar: bar),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
        };
        using var repeat = new RepeatModeController();
        repeat.Initialize(context);

        bar.ShowButtons(
            KeyboardEdgeButtons.Definitions(600), KeyboardAnchor, EdgeBarOwner.Keyboard);
        Check.That(bar.ShownButtonIds)
             .ContainsExactly(KeyboardEdgeButtons.CloseId, KeyboardEdgeButtons.FlipId);

        im.EnterRepeatMode(InteractionAction.LeftClick);
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(bar.ShownButtonIds).ContainsExactly("repeat-stop");
        var stopCentre = bar.EdgeBarVm!.SeedCenters["repeat-stop"];
        Check.WithCustomMessage("the repeat entry did not draw the stop on the shared exit anchor")
             .That(stopCentre).IsEqualTo(
                 RepeatModeController.ExitAnchorPhysical
                 + new PhysicalPoint(0, RepeatModeController.StopButtonVerticalOffsetPhysical));
        // The tick-suppression zone is appended after the toolbar band(s); it must stay centred on
        // the drawn stop button, not on where the button used to sit.
        var repeatZones = im.RepeatSuppressionZonesForTest;
        Check.That(repeatZones).Not.IsEmpty();
        Check.WithCustomMessage(
                $"the repeat suppression zone centre is ({repeatZones[^1].Center.X:F1},"
                + $"{repeatZones[^1].Center.Y:F1}) but the button is at "
                + $"({stopCentre.X:F1},{stopCentre.Y:F1})")
             .That(repeatZones[^1].Center).IsEqualTo(stopCentre);

        im.ExitRepeatMode();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.WithCustomMessage($"bar holds [{string.Join(",", bar.ShownButtonIds)}]")
             .That(bar.ShownButtonIds)
             .ContainsExactly(KeyboardEdgeButtons.CloseId, KeyboardEdgeButtons.FlipId);
    }

    [StaFact]
    public void RepeatEnterFailureAfterTheClaim_ReleasesTheClaim()
    {
        // F6: the stop button is claimed before the rest of Enter runs. A throw after that claim
        // (zone build, logging, ...) must not leave it on the shared bar — no state change would
        // ever call Exit for a mode that never finished entering, so the dead stop would come back
        // on the next owner's release.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var bar = new TestFakes.FakeEdgeButtonBar
        {
            ShowButtonsFailureAfterClaim = new InvalidOperationException("the suppression zone build failed"),
        };
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(edgeButtonBar: bar),
            InteractionManager = new InteractionManager(),
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
        };
        using var repeat = new RepeatModeController();
        repeat.Initialize(context);

        context.InteractionManager.EnterRepeatMode(InteractionAction.LeftClick);
        try { Pump(dispatcher); } catch (InvalidOperationException) { /* the entering failure */ }
        Pump(dispatcher);

        Check.WithCustomMessage(
                $"bar holds [{string.Join(",", bar.ShownButtonIds)}] after calls "
                + $"[{string.Join(",", bar.Calls)}]")
             .That(bar.Calls).Contains("Hide");
        Check.That(bar.EdgeBarVm!.CurrentOwner).IsNull();
        Check.That(bar.EdgeBarVm.Buttons).IsEmpty();
        // R2: the failed entry must END the mode, not leave the user in it with no out slot.
        Check.That(context.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [StaFact]
    public void ScrollEnterFailureAfterTheClaim_ReleasesTheClaim()
    {
        // Same F6 guarantee on the scroll entry: its window/worker steps run after the claim.
        var bar = new TestFakes.FakeEdgeButtonBar
        {
            ShowButtonsFailureAfterClaim = new InvalidOperationException("the scroll window refused to build"),
        };
        var (controller, im, _, _) =
            ScrollModeControllerThreadingTests.NewScrollWorld(
                new TestFakes.FakeInputSimulator(), edgeBar: bar);
        using (controller)
        {
            try { ScrollModeControllerThreadingTests.EnterScrollMode(im); }
            catch (InvalidOperationException) { /* the entering failure */ }

            Check.WithCustomMessage(
                    $"bar holds [{string.Join(",", bar.ShownButtonIds)}] after calls "
                    + $"[{string.Join(",", bar.Calls)}]")
                 .That(bar.Calls).Contains("Hide");
            Check.That(bar.EdgeBarVm!.CurrentOwner).IsNull();
            Check.That(bar.EdgeBarVm.Buttons).IsEmpty();
            // R2: a failed entry must END the mode, leaving the user in FreeLook.
            Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
            Check.That(controller.IsActive).IsFalse();
        }
    }

    [StaFact]
    public void ScrollFailureAfterTheActiveFlag_EndsTheMode()
    {
        // R2: the ShowButtons cases throw BEFORE the active flag. This one throws after it — the
        // scroll window cannot be built — so the early flag and the catch must leave FreeLook with
        // an empty bar, not a live scroll mode whose stop button is gone.
        var bar = new TestFakes.FakeEdgeButtonBar();
        var (controller, im, _, _) =
            ScrollModeControllerThreadingTests.NewScrollWorld(
                new TestFakes.FakeInputSimulator(),
                edgeBar: bar,
                createOverlayWindow: static () =>
                    throw new InvalidOperationException("the scroll window refused to build"));
        using (controller)
        {
            Exception? thrown = null;
            try { ScrollModeControllerThreadingTests.EnterScrollMode(im); }
            catch (InvalidOperationException ex) { thrown = ex; }

            Check.That(thrown).IsNotNull();
            Check.WithCustomMessage(
                    $"a failed scroll entry left the state at {im.CurrentState}")
                 .That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
            Check.That(controller.IsActive).IsFalse();
            Check.That(bar.EdgeBarVm!.CurrentOwner).IsNull();
            Check.That(bar.EdgeBarVm.Buttons).IsEmpty();
        }
    }

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
