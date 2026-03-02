namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly ToolbarSettings _toolbarSettings = new() { Size = 80 };
    private readonly DwellSettings _dwellSettings = new() { ToolbarButtonDwellMs = 100 };

    [Fact]
    public void InitializesWith4Buttons()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        Assert.Equal(4, vm.Buttons.Count);
    }

    [Fact]
    public void PauseButton_SendsPauseToggledMessage()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
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
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        Assert.False(vm.IsPaused);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.True(vm.IsPaused);

        // Pause button symbol should change to play ▶
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Assert.Equal("\u25B6", pauseBtn.Symbol);
    }

    [Fact]
    public void ZoomToggle_SendsZoomToggledMessage()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        bool received = false;
        _messenger.Register<ZoomToggledMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("Zoom");

        Assert.True(received);
    }
}
