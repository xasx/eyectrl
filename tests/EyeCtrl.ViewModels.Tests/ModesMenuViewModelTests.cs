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
    public void RowDefinitions_ListTheFourClickActions()
    {
        var (vm, _) = Make();
        Check.That(vm.RowDefinitions.Select(r => r.Action)).ContainsExactly(
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.MiddleClick);
    }

    [Fact]
    public void StateChanged_TracksTheLockedAction()
    {
        var (vm, messenger) = Make();
        Check.That(vm.ActiveAction).IsNull();

        // The menu is the only entry point, so it knows what it locked: lock a row first, then
        // the state message flips the marker on/off.
        vm.ActivateForTest("modes_left");
        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));
        Check.That(vm.ActiveAction).IsEqualTo(InteractionAction.LeftClick);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.ActiveAction).IsNull();
    }

    [Fact]
    public void DwellingARow_WhenModeOff_SendsEnterWithThatAction_AndHides()
    {
        var (vm, messenger) = Make();
        messenger.Send(new ModesMenuToggledMessage());

        EnterRepeatModeMessage? sent = null;
        messenger.Register<object, EnterRepeatModeMessage>(this, (_, m) => sent = m);

        vm.ActivateForTest("modes_double");

        Check.That(sent).IsNotNull();
        Check.That(sent!.Action).IsEqualTo(InteractionAction.DoubleClick);
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void DwellingTheActiveRow_WhenModeOn_SendsExit()
    {
        var (vm, messenger) = Make();
        vm.ActivateForTest("modes_middle");                        // locks Middle
        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));   // mode runs

        var exited = false;
        messenger.Register<object, ExitRepeatModeMessage>(this, (_, _) => exited = true);

        vm.ActivateForTest("modes_middle");                        // same row = exit

        Check.That(exited).IsTrue();
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void DwellingADifferentRow_WhileOn_SwitchesTheLockedAction()
    {
        var (vm, messenger) = Make();
        vm.ActivateForTest("modes_middle");
        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));

        EnterRepeatModeMessage? sent = null;
        messenger.Register<object, EnterRepeatModeMessage>(this, (_, m) => sent = m);

        vm.ActivateForTest("modes_left");

        Check.That(sent).IsNotNull();
        Check.That(sent!.Action).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void Hiding_ClearsTheDwellTargets()
    {
        var (vm, messenger) = Make();
        messenger.Send(new ModesMenuToggledMessage());
        vm.SetRowScreenPositions(new[]
        {
            ("modes_left", new PhysicalPoint(500, 200), 60.0),
            ("modes_right", new PhysicalPoint(500, 250), 60.0),
            ("modes_double", new PhysicalPoint(500, 300), 60.0),
            ("modes_middle", new PhysicalPoint(500, 350), 60.0),
        });

        messenger.Send(new ModesMenuToggledMessage());
        Check.That(vm.RowDwellProgress).IsEqualTo(0);
    }
}
