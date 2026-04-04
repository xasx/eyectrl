namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Manages the interaction state machine for gaze-based interactions.
/// All point coordinates are in physical screen pixels.
/// </summary>
public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public bool ZoomEnabled { get; set; }
    /// <summary>Initial fixation point in physical screen pixels.</summary>
    public PhysicalPoint? FixationPoint { get; private set; }
    /// <summary>Precise zoom fixation point in physical screen pixels.</summary>
    public PhysicalPoint? ZoomFixationPoint { get; private set; }

    public event Action<InteractionState>? StateChanged;
    public event Action<InteractionAction, PhysicalPoint>? ActionRequested;
    public event Action<PhysicalPoint>? ShowActivationButton;
    public event Action? HideActivationButton;
    public event Action<PhysicalPoint>? ShowActionMenu;
    public event Action? HideActionMenu;
    public event Action<PhysicalPoint>? StartZoom;
    public event Action? EndZoom;
    public event Action? HideZoom;

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

        if (ZoomEnabled)
        {
            StartZoom?.Invoke(FixationPoint!.Value);
            SetState(InteractionState.ZoomSelectionActive);
        }
        else
        {
            ShowActionMenu?.Invoke(FixationPoint!.Value);
            SetState(InteractionState.ActionMenuVisible);
        }
    }

    /// <summary>
    /// Called when zoom fixation completes.
    /// </summary>
    /// <param name="precisePoint">Precise fixation point in physical screen pixels</param>
    public void OnZoomFixation(PhysicalPoint precisePoint)
    {
        if (CurrentState != InteractionState.ZoomSelectionActive) return;
        ZoomFixationPoint = precisePoint;
        HideZoom?.Invoke();
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ZoomSelectionActionMenuVisible);
    }

    public void OnActionSelected(InteractionAction action)
    {
        PhysicalPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible => FixationPoint,
            InteractionState.ZoomSelectionActionMenuVisible => ZoomFixationPoint,
            _ => null
        };

        if (targetPoint == null) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ZoomSelectionActionMenuVisible)
            EndZoom?.Invoke();

        ActionRequested?.Invoke(action, targetPoint.Value);
        ResetToFreeLook();
    }

    public void OnZoomBack()
    {
        if (CurrentState == InteractionState.ZoomSelectionActionMenuVisible)
        {
            // Back from action view → return to zoom selection
            HideActionMenu?.Invoke();
            ZoomFixationPoint = null;
            SetState(InteractionState.ZoomSelectionActive);
            StartZoom?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ZoomSelectionActive)
        {
            // Close from zoom view → return to free look
            EndZoom?.Invoke();
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
            if (CurrentState is InteractionState.ZoomSelectionActive or InteractionState.ZoomSelectionActionMenuVisible)
                EndZoom?.Invoke();
            SetState(InteractionState.Paused);
        }
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ZoomFixationPoint = null;
        SetState(InteractionState.FreeLook);
    }
}
