namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The ring's two layers and the three non-action affordances that navigate between them.
///
/// <c>IsBackButton</c> was a bool until 2026-08-26, which was enough while Back was the only
/// non-action item. It is not enough now: the zoom Back leaves the menu, the layer Back returns
/// to layer one, and More opens layer two. All three are "not an action", and a bool would let
/// two of them be mistaken for each other at the one place that decides what a dwell DOES.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ActionMenuViewModelLayerTests
{
    private static EyeCtrlSettings Defaults() => new();

    private static void Show(IMessenger messenger, bool showBack = false) =>
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            Defaults().ActionMenu.VisibleActions,
            ShowBack: showBack,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

    [Fact]
    public void AnActionItem_IsKindAction()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger);

        var leftClick = vm.Items.Single(i => i.Action == InteractionAction.LeftClick);
        Check.That(leftClick.Kind).IsEqualTo(ActionMenuItemKind.Action);
    }

    [Fact]
    public void TheZoomBackItem_IsKindExitMenu()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger, showBack: true);

        Check.That(vm.Items.Any(i => i.Kind == ActionMenuItemKind.ExitMenu)).IsTrue();
    }

    [Fact]
    public void TheFirstLayer_ShowsFourActionsAndMore()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);

        Show(messenger);

        Check.That(vm.Items.Count).IsEqualTo(5);
        Check.That(vm.Items.Where(i => i.Kind == ActionMenuItemKind.Action).Select(i => i.Action))
             .ContainsExactly(
                 InteractionAction.LeftClick, InteractionAction.RightClick,
                 InteractionAction.DoubleClick, InteractionAction.ScrollMode);
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void DwellingMore_OpensTheSecondLayerWithoutClosingTheMenu()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        var more = vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore);
        vm.ActivateForTest(more.Id);

        Check.WithCustomMessage("More closed the menu").That(vm.IsVisible).IsTrue();
        Check.That(vm.Items.Where(i => i.Kind == ActionMenuItemKind.Action).Select(i => i.Action))
             .ContainsExactly(
                 InteractionAction.Drag, InteractionAction.ScrollUp,
                 InteractionAction.ScrollDown, InteractionAction.PinNote,
                 InteractionAction.Keyboard);
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.BackToPrimary)).IsEqualTo(1);
    }

    [Fact]
    public void TheSecondLayer_GrowsToSixSlots_OnOneRingWithEvenGaps()
    {
        // Since the Keyboard action joined the default second layer (spec ledger #3), the
        // layers hold different slot counts (5 vs 6) and the old "Back lands exactly where
        // More was" symmetry is knowingly given up — with 6 slots More's old 72° slot
        // becomes 60°, so no slot can re-occupy a layer-one angle. What IS load-bearing:
        // every layer-two target sits at exactly the ring radius from the menu centre, the
        // six angular gaps are even (angleStep re-divided by the new count — the regression
        // the deleted slot-symmetry fact used to catch), and the Back target still exists.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);
        var layerTwo = vm.Items.ToList();

        Check.That(layerTwo).HasSize(6);
        Check.That(layerTwo.Count(i => i.Kind == ActionMenuItemKind.BackToPrimary)).IsEqualTo(1);

        // Every slot sits at exactly the ring radius from the menu centre (900, 500 — the
        // Show() helper's centre)...
        var expectedRadius = DpiHelper.LogicalToPhysicalX(vm.EffectiveRingRadius);
        Check.That(layerTwo.Select(i => DistanceFrom(i.Position)))
             .ContainsOnlyElementsThatMatch(d => Math.Abs(d - expectedRadius) < 1e-6);

        var angles = layerTwo.Select(i => AngleFromCenter(i.Position)).OrderBy(a => a).ToList();
        for (var i = 0; i < angles.Count; i++)
        {
            var next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 360;
            Check.That(next - angles[i]).IsCloseTo(60, 1e-6);
        }
    }

    private static double AngleFromCenter(PhysicalPoint p) =>
        Math.Atan2(p.Y - 500, p.X - 900) * 180 / Math.PI;

    private static double DistanceFrom(PhysicalPoint p) =>
        Math.Sqrt(Math.Pow(p.X - 900, 2) + Math.Pow(p.Y - 500, 2));

    [Fact]
    public void DwellingBackOnTheSecondLayer_ReturnsToTheFirst()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.BackToPrimary).Id);

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void ReopeningTheMenu_AlwaysStartsOnTheFirstLayer()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        messenger.Send(new HideActionMenuMessage());
        Show(messenger);

        Check.WithCustomMessage("The menu reopened on layer two")
             .That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.OpenMore)).IsEqualTo(1);
    }

    [Fact]
    public void WithNoSecondaryActions_ThereIsNoMoreTarget()
    {
        var settings = Defaults();
        settings.ActionMenu.VisibleActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
        };
        settings.ActionMenu.PrimaryActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
        };

        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(settings, messenger);
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            settings.ActionMenu.VisibleActions,
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

        Check.WithCustomMessage("A More target that opens an empty layer is a dead end")
             .That(vm.Items.Any(i => i.Kind == ActionMenuItemKind.OpenMore)).IsFalse();
        Check.That(vm.Items.Count).IsEqualTo(2);
    }

    [Fact]
    public void ALayerChange_RaisesRingVersion()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);
        var before = vm.RingVersion;

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        Check.WithCustomMessage(
                "Nothing told the window to redraw. The DwellDetector now targets layer two while "
                + "the canvas still shows layer one — every slot fires the wrong action.")
            .That(vm.RingVersion).IsStrictlyGreaterThan(before);
    }

    [Fact]
    public void ALayerChange_PublishesRingVersionAfterTheItems()
    {
        // RingVersion is the redraw signal, and PropertyChanged is raised SYNCHRONOUSLY: the
        // subscriber (OverlayVmBindingCoordinator) enumerates Items from inside the handler to
        // draw the ring. Bumping the counter before the items are added therefore hands it an
        // empty collection and draws layer two as a bare circle.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        var itemsSeenByTheRedrawHandler = -1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionMenuViewModel.RingVersion))
                itemsSeenByTheRedrawHandler = vm.Items.Count;
        };

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        Check.WithCustomMessage("The redraw handler saw an empty ring — RingVersion was bumped "
                                + "before Items was filled.")
             .That(itemsSeenByTheRedrawHandler).IsEqualTo(6);
    }
}
