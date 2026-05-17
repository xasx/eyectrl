namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Manages the interaction state machine for gaze-based interactions.
/// All point coordinates are in physical screen pixels.
/// </summary>
public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public bool ManualSelectionEnabled { get; set; }
    /// <summary>Initial fixation point in physical screen pixels.</summary>
    public PhysicalPoint? FixationPoint { get; private set; }
    /// <summary>Precise ManualSelection dwell point in physical screen pixels.</summary>
    public PhysicalPoint? ManualSelectionDwellPoint { get; private set; }

    public event Action<InteractionState>? StateChanged;
    public event Action<InteractionAction, PhysicalPoint>? ActionRequested;
    public event Action<PhysicalPoint>? ShowActivationButton;
    public event Action? HideActivationButton;
    public event Action<PhysicalPoint>? ShowActionMenu;
    public event Action? HideActionMenu;
    public event Action<PhysicalPoint>? StartManualSelection;
    public event Action? EndManualSelection;
    public event Action? HideManualSelection;

    private void SetState(InteractionState newState)
    {
        CurrentState = newState;
        StateChanged?.Invoke(newState);
    }

    /// <summary>
    /// Called when fixation starts.
    /// </summary>
    /// <param name="point">Fixation point in physical screen pixels</param>
    public void OnFixationStarted(PhysicalPoint point)
    {
        if (CurrentState != InteractionState.FreeLook) return;
        FixationPoint = point;
        SetState(InteractionState.Fixation);
        ShowActivationButton?.Invoke(point);
    }

    public void OnFixationEnded()
    {
        if (CurrentState == InteractionState.Fixation)
        {
            HideActivationButton?.Invoke();
            SetState(InteractionState.FreeLook);
        }
    }

    public void OnActivationButtonDwelled()
    {
        if (CurrentState != InteractionState.Fixation) return;
        HideActivationButton?.Invoke();

        if (ManualSelectionEnabled)
        {
            StartManualSelection?.Invoke(FixationPoint!.Value);
            SetState(InteractionState.ManualSelectionActive);
        }
        else
        {
            ShowActionMenu?.Invoke(FixationPoint!.Value);
            SetState(InteractionState.ActionMenuVisible);
        }
    }

    /// <summary>
    /// Called when ManualSelection dwell completes.
    /// </summary>
    /// <param name="precisePoint">Precise dwell point in physical screen pixels</param>
    public void OnManualSelectionDwell(PhysicalPoint precisePoint)
    {
        if (CurrentState != InteractionState.ManualSelectionActive) return;
        ManualSelectionDwellPoint = precisePoint;
        HideManualSelection?.Invoke();
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ManualSelectionActionMenuVisible);
    }

    public void OnActionSelected(InteractionAction action)
    {
        PhysicalPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible => FixationPoint,
            InteractionState.ManualSelectionActionMenuVisible => ManualSelectionDwellPoint,
            _ => null
        };

        if (targetPoint == null) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();

        ActionRequested?.Invoke(action, targetPoint.Value);
        ResetToFreeLook();
    }

    public void OnManualSelectionBack()
    {
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        {
            // Back from action view → return to ManualSelection selection
            HideActionMenu?.Invoke();
            ManualSelectionDwellPoint = null;
            SetState(InteractionState.ManualSelectionActive);
            StartManualSelection?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ManualSelectionActive)
        {
            // Close from ManualSelection view → return to free look
            EndManualSelection?.Invoke();
            ResetToFreeLook();
        }
    }

    public void OnActionMenuDismissed()
    {
        if (CurrentState == InteractionState.ActionMenuVisible)
        {
            HideActionMenu?.Invoke();
            ResetToFreeLook();
        }
    }

    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else
        {
            HideActivationButton?.Invoke();
            HideActionMenu?.Invoke();
            if (CurrentState is InteractionState.ManualSelectionActive or InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            SetState(InteractionState.Paused);
        }
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ManualSelectionDwellPoint = null;
        SetState(InteractionState.FreeLook);
    }
}
