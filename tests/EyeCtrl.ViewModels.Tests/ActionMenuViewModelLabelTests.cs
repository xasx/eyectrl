namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Labels are off until the Help target is dwelled, and they do not survive the menu closing.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ActionMenuViewModelLabelTests
{
    private static void Show(IMessenger messenger) =>
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(900, 500),
            new EyeCtrlSettings().ActionMenu.VisibleActions,
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(900, 500),
            CircleSize: 250));

    [Fact]
    public void LabelsAreOff_WhenTheMenuOpens()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        Show(messenger);

        Check.That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void TheToggleMessage_TurnsLabelsOn()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        messenger.Send(new ToggleActionMenuLabelsMessage());

        Check.That(vm.ShowLabels).IsTrue();
    }

    [Fact]
    public void TheToggleMessage_TurnsThemOffAgain()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        messenger.Send(new ToggleActionMenuLabelsMessage());
        messenger.Send(new ToggleActionMenuLabelsMessage());

        Check.That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void LabelsSurviveALayerChange()
    {
        // Switching to More is not a reason to lose the names — the second layer is exactly where
        // a user who just asked what things are called still needs them.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);
        messenger.Send(new ToggleActionMenuLabelsMessage());

        vm.ActivateForTest(vm.Items.Single(i => i.Kind == ActionMenuItemKind.OpenMore).Id);

        Check.That(vm.ShowLabels).IsTrue();
    }

    [Fact]
    public void ClosingTheMenu_ForgetsTheLabels()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);
        messenger.Send(new ToggleActionMenuLabelsMessage());

        messenger.Send(new HideActionMenuMessage());
        Show(messenger);

        Check.WithCustomMessage("Labels persisted across a menu session. That is a settings-level "
                                + "choice and is not what this toggle is.")
             .That(vm.ShowLabels).IsFalse();
    }

    [Fact]
    public void EveryItem_CarriesANameWhetherOrNotItIsShown()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        Show(messenger);

        foreach (var item in vm.Items)
            Check.WithCustomMessage($"{item.Id} has no label")
                 .That(string.IsNullOrWhiteSpace(item.Label)).IsFalse();
    }
}
