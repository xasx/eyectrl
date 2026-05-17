namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void InitializesWith4Buttons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Assert.Equal(4, vm.Buttons.Count);
    }

    [Fact]
    public void PauseButton_SendsPauseToggledMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<PauseToggledMessage>(_messenger, (_, _) => received = true);

        // Find the Pause button and simulate activation
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        vm.SimulateButtonActivated("Pause");

        Assert.True(received);
    }

    [Fact]
    public void UpdatesPausedState_OnStateChanged()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Assert.False(vm.IsPaused);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.True(vm.IsPaused);

        // Pause button symbol should change to play ▶
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Assert.Equal("\u25B6", pauseBtn.Symbol);
    }

    [Fact]
    public void ManualSelectionToggle_SendsManualSelectionToggledMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<ManualSelectionToggledMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("ManualSelection");

        Assert.True(received);
    }
}
