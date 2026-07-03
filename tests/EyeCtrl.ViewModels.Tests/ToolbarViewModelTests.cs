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
        Check.That(vm.Buttons.Count).IsEqualTo(5);
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

        Check.That(received).IsTrue();
    }

    [Fact]
    public void UpdatesPausedState_OnStateChanged()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.IsPaused).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsPaused).IsTrue();

        // Pause button symbol should change to play ▶
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Check.That(pauseBtn.Symbol).IsEqualTo("\u25B6");
    }

    [Fact]
    public void SelectionModeButton_SendsSelectionModeChangedMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        SelectionMode? received = null;
        _messenger.Register<SelectionModeChangedMessage>(_messenger, (_, msg) => received = msg.Mode);

        vm.SimulateButtonActivated("SelectionMode");

        Check.That(received).IsNotNull();
        Check.That(received).IsEqualTo(SelectionMode.ManualSelection);
    }

    [Fact]
    public void SelectionModeChanged_UpdatesSelectionButtonSymbol()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        var selectionButton = vm.Buttons.First(b => b.Id == "SelectionMode");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.None));
        Check.That(selectionButton.Symbol).IsEqualTo("\u25CB");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.ManualSelection));
        Check.That(selectionButton.Symbol).IsEqualTo("\u2295");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.AutoSelection));
        Check.That(selectionButton.Symbol).IsEqualTo("\u25CE");
    }

    [Fact]
    public void SelectionModeCycle_VisitsEveryEnumValueAndWraps()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var seen = new List<SelectionMode>();
        messenger.Register<SelectionModeChangedMessage>(this, (_, m) =>
        {
            seen.Add(m.Mode);
            vm.Receive(m);
        });

        int count = Enum.GetValues<SelectionMode>().Length;
        for (int i = 0; i <= count; i++)
            vm.SimulateButtonActivated("SelectionMode");

        Check.That(seen.Distinct().Count()).IsEqualTo(count);
        Check.That(seen[count]).IsEqualTo(seen[0]);
    }
}
