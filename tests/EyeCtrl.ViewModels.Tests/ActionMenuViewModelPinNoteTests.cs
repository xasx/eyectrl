namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

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

        Assert.Single(vm.Items);
        Assert.NotNull(vm.Items[0].Action);
        Assert.Equal(InteractionAction.PinNote, vm.Items[0].Action!.Value);
        Assert.NotEmpty(vm.Items[0].Symbol);
        Assert.NotEqual("PinNote", vm.Items[0].Symbol);
    }
}
