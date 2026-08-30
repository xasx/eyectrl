namespace EyeCtrl.App.Tests;

using System.Linq;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Task 8: a brief success-coloured flash confirms a dwell actually fired, so completing a dwell
/// on Settings or Windows (whose effect happens somewhere the user isn't looking) doesn't look
/// identical to a dwell abandoned at 99% — both zero <c>DwellProgress</c> and collapse the arc.
///
/// <see cref="ToolbarWindow.PaintFace"/> is the sole writer of a disc's Fill/Stroke (Task 5), so
/// the flash is a third INPUT to that method (a <c>_flashingIds</c> set) rather than a third
/// writer of the two properties directly — flash beats acquired beats rest.
///
/// <see cref="ToolbarWindow.EndFlash"/> is driven directly rather than via the real
/// <c>DispatcherTimer</c>: these tests construct the window on an STA thread with no message pump
/// running (nothing calls <c>Show()</c> — see <c>ToolbarButtonVisualTests</c>'s header comment),
/// so a <c>DispatcherTimer</c>'s <c>Tick</c> never fires here. A test that waited on the real
/// timer would hang, not merely be slow.
/// </summary>
[Collection("WpfApplication")]
public class ToolbarActivationFlashTests
{
    [StaFact]
    public void Activation_FlashesTheSuccessColour_ThenReturnsToRest()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var face = window.ButtonForTest("Pause").Background;

        vm.SimulateButtonActivated("Pause");

        Check.WithCustomMessage("Activation did not paint the success fill.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfacePrediction);
        Check.WithCustomMessage("Activation did not paint the success rim.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentSuccess);

        window.EndFlash("Pause");

        Check.WithCustomMessage("The face did not return to rest fill after the flash ended.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);
        Check.WithCustomMessage("The face did not return to rest rim after the flash ended.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderSubtle);
    }

    // The interesting precedence: PaintFace now has three inputs (flash, acquired, rest), and
    // flash must win over acquired, not just over rest. A test that only ever activated a
    // never-highlighted button could not distinguish "flash beats rest" from "flash beats
    // acquired" — both would show the flash colours.
    //
    // IsHighlighted is set true AFTER SimulateButtonActivated, not before: ToolbarViewModel's own
    // OnTargetActivated resets IsHighlighted to false as part of activating (ToolbarViewModel.cs,
    // ahead of raising ButtonActivated), so setting it beforehand would just be undone by the
    // activation itself and prove nothing about precedence. Setting it true afterward simulates
    // gaze re-acquiring the button on its very next sample, which is the realistic case — the
    // user is usually still looking at what they just activated.
    [StaFact]
    public void Activation_WhileAcquired_ShowsTheFlashColour_NotTheAcquiredColour()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var button = vm.Buttons.Single(b => b.Id == "Pause");
        var face = window.ButtonForTest("Pause").Background;

        vm.SimulateButtonActivated("Pause");
        button.IsHighlighted = true;

        Check.WithCustomMessage("Flash must beat acquired, not the other way round.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfacePrediction);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentSuccess);
    }

    // The case the plan's literal EndFlashForTest (calling ApplyAcquiredState(id, false)) would
    // have got wrong: if gaze is STILL on the button when the flash ends, the face must return to
    // acquired, not to rest. ApplyAcquiredState(id, false) would have cleared _acquiredIds itself,
    // silently losing the acquired state instead of merely repainting.
    //
    // Same reasoning as the test above for setting IsHighlighted AFTER activation: activation's
    // own IsHighlighted=false reset happens first, so this simulates re-acquisition on the next
    // gaze sample rather than gaze that was already there.
    [StaFact]
    public void EndingAFlash_WhileGazeIsStillOnTheButton_ReturnsToAcquired_NotRest()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var button = vm.Buttons.Single(b => b.Id == "Pause");
        var face = window.ButtonForTest("Pause").Background;

        vm.SimulateButtonActivated("Pause");
        button.IsHighlighted = true;
        window.EndFlash("Pause");

        Check.WithCustomMessage("Ending the flash dropped the acquired fill although gaze never left.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceLight);
        Check.WithCustomMessage("Ending the flash dropped the acquired rim although gaze never left.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderFocus);
    }

    // Reset's rim is warning-coloured at rest (Task 7), not BorderSubtle. RimFor already
    // special-cases it, and PaintFace must still respect that special case on the way OUT of a
    // flash — a naive "flash ends -> BorderSubtle" would silently erase the one visual marker on
    // the toolbar's unrecoverable action.
    [StaFact]
    public void EndingAFlash_OnReset_ReturnsToTheWarningRim_NotBorderSubtle()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var face = window.ButtonForTest(ToolbarWindow.ResetId).Background;

        vm.SimulateButtonActivated(ToolbarWindow.ResetId);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentSuccess);

        window.EndFlash(ToolbarWindow.ResetId);

        Check.WithCustomMessage("Reset's warning rim did not return after its flash ended.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentWarning);
    }

    // A second activation while the first flash is still live must leave the face flashed rather
    // than reverting early, and a subsequent EndFlash must still cleanly return it to rest. This
    // does NOT prove the first timer was stopped before the second replaced it — deleting that
    // stop-before-replace still leaves this test (and the other four in this class) green, so the
    // name promises only what is actually observed here.
    [StaFact]
    public void ASecondActivation_DuringALiveFlash_LeavesTheFaceFlashed()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var face = window.ButtonForTest("Pause").Background;

        vm.SimulateButtonActivated("Pause");
        vm.SimulateButtonActivated("Pause");

        Check.WithCustomMessage("A second activation during a live flash should leave the face flashed.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfacePrediction);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentSuccess);

        // EndFlash must still cleanly return to rest afterward - if the first activation's timer
        // had been orphaned rather than stopped, this could throw or double-remove.
        window.EndFlash("Pause");
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);
    }
}
