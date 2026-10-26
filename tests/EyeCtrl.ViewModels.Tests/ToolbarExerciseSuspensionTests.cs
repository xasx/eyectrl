namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// B7 fixup-02: while an eye-care exercise (or its summary) owns the screen, the toolbar takes no
/// dwell. The band may still be visible for up to its 5 s auto-hide delay, but the exercise window
/// is opaque and full-screen, so a button firing under it would be invisible input; the exercise
/// also asks for a deliberate 0.7 s rest on its own Skip disc. The suspension is its own flag, not
/// <c>SetActive</c>, because the auto-hide adapter re-asserts that on every show/hide.
/// </summary>
public class ToolbarExerciseSuspensionTests
{
    private static readonly PhysicalPoint On = new(500, 500);

    private static (ToolbarViewModel Vm, List<string> Fired) NewToolbar()
    {
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.UpdateButtonScreenPosition("Pause", On);
        var fired = new List<string>();
        vm.ButtonActivated += fired.Add;
        return (vm, fired);
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
        // The fixup's acceptance: the band is visible (the VM starts dwell-active) when the
        // exercise starts; 3 s of rest fires nothing. Mutation: drop !_exerciseSuspended from
        // DwellEnabled -> Pause fires.
        var (vm, fired) = NewToolbar();
        using var _ = vm;

        vm.SetExerciseSuspended(true);
        Rest(vm, 0, 3000);

        Check.WithCustomMessage("a toolbar button fired under the opaque exercise window")
            .That(fired).IsEmpty();
        Check.That(vm.Buttons.Single(b => b.Id == "Pause").DwellProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void AfterTheExerciseEnds_TheToolbarDwellsNormallyAgain()
    {
        var (vm, fired) = NewToolbar();
        using var _ = vm;

        vm.SetExerciseSuspended(true);
        Rest(vm, 0, 3000);
        Check.That(fired).IsEmpty();

        vm.SetExerciseSuspended(false);
        Rest(vm, 3030, 6000);

        Check.WithCustomMessage("the toolbar did not dwell again after the exercise ended")
            .That(fired).ContainsExactly("Pause");
    }

    [Fact]
    public void EndingTheExercise_DoesNotForceShowAHiddenToolbar()
    {
        // Restore means "whatever the auto-hide adapter says": clearing the exercise flag over a
        // hidden band must not re-enable dwell (that is how a hidden toolbar would start firing
        // from former screen positions). A later adapter show does.
        var (vm, fired) = NewToolbar();
        using var _ = vm;
        vm.SetActive(false);              // the auto-hide lifecycle hid the band

        vm.SetExerciseSuspended(true);
        vm.SetExerciseSuspended(false);
        Rest(vm, 0, 3000);
        Check.WithCustomMessage("clearing the exercise flag overrode the adapter's hidden state")
            .That(fired).IsEmpty();

        vm.SetActive(true);
        Rest(vm, 3030, 6000);
        Check.That(fired).ContainsExactly("Pause");
    }

    [Fact]
    public void SuspendingMidDwell_DropsTheHeldProgress()
    {
        // A rest that began before the exercise must not resume across it: the progress drops at
        // the start, and the cleared anchor means no instant fire from the stale timestamp either.
        var (vm, fired) = NewToolbar();
        using var _ = vm;

        Rest(vm, 0, 400);                 // ~0.57 of the 700 ms Pause dwell
        var button = vm.Buttons.Single(b => b.Id == "Pause");
        Check.That(button.DwellProgress).IsStrictlyGreaterThan(0.0);

        vm.SetExerciseSuspended(true);
        Check.WithCustomMessage("a mid-flight dwell survived the exercise start")
            .That(button.DwellProgress).IsEqualTo(0.0);

        Rest(vm, 430, 1300);
        Check.WithCustomMessage("the pre-exercise rest completed after the suspension").That(fired).IsEmpty();
    }
}
