namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// B7 fixup-03: while an eye-care exercise (or its summary) owns the screen, the toolbar takes no
/// dwell. The gate is the shared <see cref="DwellOverlayViewModelBase"/> hold
/// (<see cref="ExerciseScreenHoldMessage"/>), so these tests drive the message the App publishes —
/// the toolbar's fixup-02 flag has been folded into it. The band may still be visible for up to
/// its 5 s auto-hide delay, but the exercise window is opaque and full-screen, so a button firing
/// under it would be invisible input; the exercise also asks for a deliberate 0.7 s rest on its
/// own Skip disc.
/// </summary>
public class ToolbarExerciseSuspensionTests
{
    private static readonly PhysicalPoint On = new(500, 500);

    private static (ToolbarViewModel Vm, WeakReferenceMessenger Messenger, List<string> Fired) NewToolbar()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), messenger);
        vm.UpdateButtonScreenPosition("Pause", On);
        var fired = new List<string>();
        vm.ButtonActivated += fired.Add;
        return (vm, messenger, fired);
    }

    /// <summary>A resting gaze on the Pause button, 30 ms samples, as the real stream delivers.</summary>
    private static void Rest(ToolbarViewModel vm, long fromMs, long toMs)
    {
        for (var t = fromMs; t <= toMs; t += 30)
            vm.Receive(new GazeCursorMovedMessage(On, t * 1000));
    }

    [Fact]
    public void AThreeSecondRestOnAToolbarButton_FiresNothing_WhileAnExerciseRuns()
    {
        // The acceptance: the band is visible (the VM starts dwell-active) when the exercise
        // starts; 3 s of rest fires nothing. Mutation: drop the shared term from SampleMayDwell
        // (or stop registering the hold message) -> Pause fires.
        var (vm, messenger, fired) = NewToolbar();
        using var _ = vm;

        messenger.Send(new ExerciseScreenHoldMessage(true));
        Rest(vm, 0, 3000);

        Check.WithCustomMessage("a toolbar button fired under the opaque exercise window")
            .That(fired).IsEmpty();
        Check.That(vm.Buttons.Single(b => b.Id == "Pause").DwellProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void AfterTheExerciseEnds_TheToolbarDwellsNormallyAgain()
    {
        var (vm, messenger, fired) = NewToolbar();
        using var _ = vm;

        messenger.Send(new ExerciseScreenHoldMessage(true));
        Rest(vm, 0, 3000);
        Check.That(fired).IsEmpty();

        messenger.Send(new ExerciseScreenHoldMessage(false));
        Rest(vm, 3030, 6000);

        Check.WithCustomMessage("the toolbar did not dwell again after the exercise ended")
            .That(fired).ContainsExactly("Pause");
    }

    [Fact]
    public void EndingTheExercise_DoesNotForceShowAHiddenToolbar()
    {
        // Restore means "whatever the auto-hide adapter says": clearing the hold over a hidden
        // band must not re-enable dwell (that is how a hidden toolbar would start firing from
        // former screen positions). A later adapter show does.
        var (vm, messenger, fired) = NewToolbar();
        using var _ = vm;
        vm.SetActive(false);              // the auto-hide lifecycle hid the band

        messenger.Send(new ExerciseScreenHoldMessage(true));
        messenger.Send(new ExerciseScreenHoldMessage(false));
        Rest(vm, 0, 3000);
        Check.WithCustomMessage("clearing the exercise hold overrode the adapter's hidden state")
            .That(fired).IsEmpty();

        vm.SetActive(true);
        Rest(vm, 3030, 6000);
        Check.That(fired).ContainsExactly("Pause");
    }

    [Fact]
    public void ThePauseDragExitZone_IsWithdrawnWhileTheExerciseRuns()
    {
        // R3 (review 02 N5): the drag-exit zone is the Pause target's other reader of the dwell
        // gate. Suspended -> no zone (a rest there must not read as the drag's Pause exit);
        // released -> the zone is back. Mutation: revert to `_dwellingActive` -> the first
        // assertion goes red.
        var (vm, messenger, _) = NewToolbar();
        using var __ = vm;

        Check.That(vm.PauseDragExitZone).IsNotNull();

        messenger.Send(new ExerciseScreenHoldMessage(true));
        Check.WithCustomMessage("the drag Pause exit survived the exercise hold")
            .That(vm.PauseDragExitZone).IsNull();

        messenger.Send(new ExerciseScreenHoldMessage(false));
        Check.WithCustomMessage("the drag Pause exit did not come back after the exercise")
            .That(vm.PauseDragExitZone).IsNotNull();
    }
}
