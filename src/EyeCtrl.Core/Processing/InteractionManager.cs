namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Manages the interaction state machine for gaze-based interactions.
/// All point coordinates are in physical screen pixels.
/// </summary>
public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    /// <summary>Active selection mode that drives the activation-button branch.</summary>
    public SelectionMode SelectionMode { get; set; } = SelectionMode.None;
    /// <summary>Initial fixation point in physical screen pixels.</summary>
    public PhysicalPoint? FixationPoint { get; private set; }
    /// <summary>Precise ManualSelection dwell point in physical screen pixels.</summary>
    public PhysicalPoint? ManualSelectionDwellPoint { get; private set; }
    /// <summary>
    /// Control resolved for Auto Selection at the current fixation, or null when none was found.
    /// Set externally (by the platform adapter) before <see cref="OnActivationButtonDwelled"/>,
    /// and cleared in <see cref="ResetToFreeLook"/> to prevent stale reuse.
    /// </summary>
    public ControlInfo? AutoSelectionTarget { get; set; }

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

    /// <summary>
    /// Called when the activation button dwell completes. Branches on <see cref="SelectionMode"/>:
    /// Manual starts the zoom overlay; Auto shows the action menu at the resolved
    /// <see cref="AutoSelectionTarget"/> (or the fixation point when none was found); None shows
    /// the action menu at the fixation point.
    /// </summary>
    public void OnActivationButtonDwelled()
    {
        if (CurrentState != InteractionState.Fixation) return;
        HideActivationButton?.Invoke();

        switch (SelectionMode)
        {
            case SelectionMode.ManualSelection:
                StartManualSelection?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ManualSelectionActive);
                break;

            case SelectionMode.AutoSelection:
                if (AutoSelectionTarget != null)
                    ShowActionMenu?.Invoke(AutoSelectionTarget.CenterPoint);
                else
                    ShowActionMenu?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ActionMenuVisible);
                break;

            default: // None
                ShowActionMenu?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ActionMenuVisible);
                break;
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

    /// <summary>
    /// Fires the chosen action at the resolved target point. In Auto Selection the click uses the
    /// snapped <see cref="AutoSelectionTarget"/> center (falling back to the fixation point when no
    /// control was resolved), so the action lands on the control rather than the raw gaze position.
    /// </summary>
    public void OnActionSelected(InteractionAction action)
    {
        PhysicalPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible
                when SelectionMode == SelectionMode.AutoSelection && AutoSelectionTarget != null
                    => AutoSelectionTarget.CenterPoint,
            InteractionState.ActionMenuVisible
                    => FixationPoint,
            InteractionState.ManualSelectionActionMenuVisible
                    => ManualSelectionDwellPoint,
            _       => null
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
        AutoSelectionTarget = null;
        SetState(InteractionState.FreeLook);
    }
}
