namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public SelectionMode SelectionMode { get; set; } = SelectionMode.None;
    public PhysicalPoint? FixationPoint { get; private set; }
    public PhysicalPoint? ManualSelectionDwellPoint { get; private set; }
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
            HideActionMenu?.Invoke();
            ManualSelectionDwellPoint = null;
            SetState(InteractionState.ManualSelectionActive);
            StartManualSelection?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ManualSelectionActive)
        {
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
