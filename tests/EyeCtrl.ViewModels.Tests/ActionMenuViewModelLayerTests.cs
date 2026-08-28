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
}
