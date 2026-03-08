namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public bool ZoomEnabled { get; set; }
    public ScreenPoint? FixationPoint { get; private set; }
    public ScreenPoint? ZoomFixationPoint { get; private set; }

    public event Action<InteractionState>? StateChanged;
    public event Action<InteractionAction, ScreenPoint>? ActionRequested;
    public event Action<ScreenPoint>? ShowActivationButton;
    public event Action? HideActivationButton;
    public event Action<ScreenPoint>? ShowActionMenu;
    public event Action? HideActionMenu;
    public event Action<ScreenPoint>? StartZoom;
    public event Action? EndZoom;

    private void SetState(InteractionState newState)
    {
        CurrentState = newState;
        StateChanged?.Invoke(newState);
    }

    public void OnFixationStarted(ScreenPoint point)
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
            StartZoom?.Invoke(FixationPoint!);
            SetState(InteractionState.ZoomSelectionActive);
        }
        else
        {
            ShowActionMenu?.Invoke(FixationPoint!);
            SetState(InteractionState.ActionMenuVisible);
        }
    }

    public void OnZoomFixation(ScreenPoint precisePoint)
    {
        if (CurrentState != InteractionState.ZoomSelectionActive) return;
        ZoomFixationPoint = precisePoint;
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ZoomSelectionActionMenuVisible);
    }

    public void OnActionSelected(InteractionAction action)
    {
        ScreenPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible => FixationPoint,
            InteractionState.ZoomSelectionActionMenuVisible => ZoomFixationPoint,
            _ => null
        };

        if (targetPoint == null) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ZoomSelectionActionMenuVisible)
            EndZoom?.Invoke();

        ActionRequested?.Invoke(action, targetPoint);
        ResetToFreeLook();
    }

    public void OnZoomBack()
    {
        if (CurrentState is InteractionState.ZoomSelectionActive or InteractionState.ZoomSelectionActionMenuVisible)
        {
            HideActionMenu?.Invoke();
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
