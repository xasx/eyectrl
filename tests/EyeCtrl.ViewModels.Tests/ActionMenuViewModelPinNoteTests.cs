namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class ActionMenuViewModelPinNoteTests
{
    [Fact]
    public void PinNote_Appears_InActionMenuItems()
    {
        var settings = new EyeCtrlSettings { Dwell = new DwellSettings() };
        // PinNote ships on the ring SECOND layer. Promote it so this test still asserts what it
        // was written to assert - that the item itself carries the PinNote icon - rather than
        // finding the More target that an all-secondary action list produces on layer one.
        settings.ActionMenu.PrimaryActions = new List<InteractionAction> { InteractionAction.PinNote };
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(settings, messenger);

        var actions = new List<InteractionAction> { InteractionAction.PinNote };
        var msg = new ShowActionMenuMessage(
            Center: new PhysicalPoint(500, 300),
            Actions: actions,
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: new PhysicalPoint(500, 300));

        vm.Receive(msg);

        Check.That(vm.Items).HasSize(1);
        Check.That(vm.Items[0].Action).IsNotNull();
        Check.That(vm.Items[0].Action!.Value).IsEqualTo(InteractionAction.PinNote);
        Check.That(vm.Items[0].Icon).IsEqualTo(ActionIcon.PinNote);
    }
}
