namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
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
                 InteractionAction.ScrollDown, InteractionAction.PinNote);
        Check.That(vm.Items.Count(i => i.Kind == ActionMenuItemKind.BackToPrimary)).IsEqualTo(1);
    }

    [Fact]
    public void TheSecondLayer_ReusesTheSameFiveSlots()
    {
        // A slot's gaze path must be identical on both layers, so Back lands exactly where More
        // was. Anything else makes the user re-aim for a target they just looked at.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(Defaults(), messenger);
        Show(messenger);

        var layerOnePositions = vm.Items.Select(i => i.Position).ToList();
        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);
        var layerTwoPositions = vm.Items.Select(i => i.Position).ToList();

        Check.That(layerTwoPositions).ContainsExactly(layerOnePositions);
    }

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
             .That(itemsSeenByTheRedrawHandler).IsEqualTo(5);
    }
}
