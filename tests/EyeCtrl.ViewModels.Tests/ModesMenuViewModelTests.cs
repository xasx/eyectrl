namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ModesMenuViewModelTests
{
    private static (ModesMenuViewModel Vm, WeakReferenceMessenger Messenger) Make()
    {
        var messenger = new WeakReferenceMessenger();
        return (new ModesMenuViewModel(new EyeCtrlSettings(), messenger), messenger);
    }

    [Fact]
    public void ToggleMessage_ShowsThenHides()
    {
        var (vm, messenger) = Make();

        messenger.Send(new ModesMenuToggledMessage());
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new ModesMenuToggledMessage());
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void StateChanged_TracksWhetherRepeatIsActive()
    {
        var (vm, messenger) = Make();
        Check.That(vm.RepeatRowStateText).IsEqualTo("off");

        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));
        Check.That(vm.RepeatRowStateText).IsEqualTo("on");

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.RepeatRowStateText).IsEqualTo("off");
    }

    [Fact]
    public void RowActivation_WhenOff_SendsEnter_AndHides()
    {
        var (vm, messenger) = Make();
        messenger.Send(new ModesMenuToggledMessage());
        Check.That(vm.IsVisible).IsTrue();

        var entered = false;
        messenger.Register<object, EnterRepeatModeMessage>(this, (_, _) => entered = true);

        vm.ActivateForTest("modes_repeat");

        Check.That(entered).IsTrue();
        Check.That(vm.IsVisible).IsFalse();   // the menu closes on selection
    }

    [Fact]
    public void RowActivation_WhenOn_SendsExit()
    {
        var (vm, messenger) = Make();
        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));   // repeat active

        var exited = false;
        messenger.Register<object, ExitRepeatModeMessage>(this, (_, _) => exited = true);

        vm.ActivateForTest("modes_repeat");

        Check.That(exited).IsTrue();
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Hiding_ClearsTheDwellTargets()
    {
        var (vm, messenger) = Make();
        messenger.Send(new ModesMenuToggledMessage());
        vm.SetRowScreenPosition(new PhysicalPoint(500, 200), 60);

        messenger.Send(new ModesMenuToggledMessage());

        // A hidden menu must hold no dwell target: gaze at where the row USED to be must be inert.
        // Driven through the base's gaze entry point — an Update that activates nothing.
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(500, 200), 0));
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(500, 200), 60_000_000));
        // No activation fired (would have hidden an already-hidden VM / sent a message): nothing
        // to observe directly, but the OnTargetActivated guard makes this safe-by-construction.
        Check.That(vm.RowDwellProgress).IsEqualTo(0);
    }
}
