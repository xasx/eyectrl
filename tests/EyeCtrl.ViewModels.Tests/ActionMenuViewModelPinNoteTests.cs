namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActionMenuViewModelPinNoteTests
{
    [Fact]
    public void PinNote_Appears_InActionMenuItems()
    {
        var settings = new EyeCtrlSettings { Dwell = new DwellSettings() };
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
        Check.That(vm.Items[0].Symbol).Not.IsEmpty();
        Check.That(vm.Items[0].Symbol).Not.Equals("PinNote");
    }
}
