namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Ratchet on the FreeLook guard in <see cref="InteractionManager.OnFixationStarted"/>: outside
/// <see cref="InteractionState.FreeLook"/> the method must be inert — it must not write
/// <see cref="InteractionManager.FixationPoint"/>, must not raise
/// <see cref="InteractionManager.FixationAccepted"/>, and must not pin the ring.
/// <para>
/// That one guard carries two independent findings. Keeping <c>FixationPoint</c> frozen for a whole
/// manual-selection session is what makes a later recapture land deterministically on the original
/// fixation point rather than somewhere that moves. It is also the only thing keeping a latent
/// defect unreachable: <c>ActivationButtonViewModel.Position</c> is set but unhandled by its
/// binding (<c>OverlayVmBindingCoordinator.cs:144-163</c>), so a re-show at a new point would move
/// the dwell target and leave the window behind. A change that weakens the guard breaks both at
/// once, silently — this test is what makes it loud.
/// </para>
/// <para>
/// The observed signal is the explicit <see cref="InteractionManager.FixationAccepted"/> event
/// (ticket 7), not the activation button's show: the show is a display event whose timing moves in
/// ticket 8, and the guard is what must stay inert. The previous pin observed ShowActivationButton,
/// which happened to be raised in the same branch.
/// </para>
/// <para>
/// States are enumerated from <see cref="InteractionState"/>, so a state added later is covered the
/// day it appears — its setup must be extended in <see cref="DriveIntoState"/>, and that throws
/// rather than silently testing FreeLook. Note the one asymmetry: <see cref="InteractionState.RepeatMode"/>
/// is inert via its own earlier branch in <c>OnFixationStarted</c>, not this guard, so deleting only
/// the guard leaves that case green while the other seven go red; deleting that branch as well turns
/// this case red too. The invariant is pinned by whichever branch actually enforces it.
/// </para>
/// <para>
/// <see cref="InteractionState.ScrollMode"/> overlaps an older, narrower test —
/// <c>InteractionManagerScrollModeTests.FixationStarted_IsIgnoredInScrollMode</c>. That test is left
/// in place; this theory is the broader pin, covering every state from one enumeration.
/// </para>
/// </summary>
public class InteractionManagerFreeLookInvariantTests
{
    public static TheoryData<InteractionState> NonFreeLookStates()
    {
        var states = new TheoryData<InteractionState>();
        foreach (var state in Enum.GetValues<InteractionState>())
        {
            if (state != InteractionState.FreeLook)
                states.Add(state);
        }
        return states;
    }

    [Theory]
    [MemberData(nameof(NonFreeLookStates))]
    public void OnFixationStarted_OutsideFreeLook_DoesNotAcceptFixationOrMoveFixationPoint(
        InteractionState state)
    {
        var manager = DriveIntoState(state);
        var fixationBefore = manager.FixationPoint;
        var fixationAcceptedFired = 0;
        var ringPinned = 0;
        manager.FixationAccepted += _ => fixationAcceptedFired++;
        manager.PinRing += _ => ringPinned++;
        manager.OnFixationStarted(new PhysicalPoint(900, 900));

        Check.WithCustomMessage(
                $"{state}: FixationAccepted was raised; OnFixationStarted must be inert outside FreeLook.")
             .That(fixationAcceptedFired).IsEqualTo(0);
        Check.WithCustomMessage(
                $"{state}: FixationPoint moved from {fixationBefore} to {manager.FixationPoint}; OnFixationStarted must be inert outside FreeLook.")
             .That(manager.FixationPoint).IsEqualTo(fixationBefore);

        // A settle is accepted in Fixation (that is the one state where it must pin), so the
        // ignored-outside-Fixation half is asserted for every OTHER state only.
        if (state == InteractionState.Fixation)
            return;
        manager.OnFixationSettled(new PhysicalPoint(900, 900));
        Check.WithCustomMessage(
                $"{state}: the ring was pinned; OnFixationSettled must be inert outside Fixation.")
             .That(ringPinned).IsEqualTo(0);
    }

    /// <summary>Drives a fresh manager into <paramref name="state"/> through the public API only.</summary>
    private static InteractionManager DriveIntoState(InteractionState state)
    {
        var fixation = new PhysicalPoint(100, 100);
        var manager = new InteractionManager();

        switch (state)
        {
            case InteractionState.Fixation:
                manager.OnFixationStarted(fixation);
                break;

            case InteractionState.ActionMenuVisible:
                manager.OnFixationStarted(fixation);
                manager.OnActivationButtonDwelled();
                break;

            case InteractionState.ManualSelectionActive:
                manager.SelectionMode = SelectionMode.ManualSelection;
                manager.OnFixationStarted(fixation);
                manager.OnActivationButtonDwelled();
                break;

            case InteractionState.ManualSelectionActionMenuVisible:
                manager.SelectionMode = SelectionMode.ManualSelection;
                manager.OnFixationStarted(fixation);
                manager.OnActivationButtonDwelled();
                manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));
                break;

            case InteractionState.ScrollMode:
                manager.OnFixationStarted(fixation);
                manager.OnActivationButtonDwelled();
                manager.OnActionSelected(InteractionAction.ScrollMode);
                break;

            case InteractionState.Dragging:
                manager.OnFixationStarted(fixation);
                manager.OnActivationButtonDwelled();
                manager.OnActionSelected(InteractionAction.Drag);
                break;

            case InteractionState.RepeatMode:
                manager.EnterRepeatMode(InteractionAction.LeftClick);
                break;

            case InteractionState.Paused:
                manager.TogglePause();
                break;

            default:
                throw new InvalidOperationException(
                    $"No setup exists to drive InteractionManager into {state}. Extend this ratchet to cover the new state.");
        }

        Check.WithCustomMessage($"Setup failed to reach {state}.")
             .That(manager.CurrentState).IsEqualTo(state);
        return manager;
    }
}
