namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Manages the interaction state machine for gaze-based interactions.
/// All point coordinates are in physical screen pixels.
/// </summary>
public class InteractionManager
{
    /// <summary>
    /// Current state of the interaction state machine.
    /// <para>
    /// Written from the dispatcher thread (<see cref="TogglePause"/>, <see cref="OnActionSelected"/>) as
    /// well as from the gaze thread, and read from both. A reader that needs more than one decision from
    /// the state must snapshot it ONCE into a local and decide from that snapshot — re-reading per
    /// decision lets a write land between the reads and makes a single unit of work take mutually
    /// inconsistent decisions (see <c>GazeEngine.OnGazePoint</c>).
    /// </para>
    /// </summary>
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
    /// <summary>Anchor point for the active gaze-scroll session, or null when not scrolling.</summary>
    public PhysicalPoint? ScrollAnchor { get; private set; }

    /// <summary>Raised when the interaction state changes.</summary>
    public event Action<InteractionState>? StateChanged;
    /// <summary>Raised when the user selects an action (click, scroll, etc.) at a specific point.</summary>
    public event Action<InteractionAction, PhysicalPoint>? ActionRequested;
    /// <summary>Raised to show the activation button at a given point.</summary>
    public event Action<PhysicalPoint>? ShowActivationButton;
    /// <summary>Raised to hide the activation button.</summary>
    public event Action? HideActivationButton;
    /// <summary>Raised to show the action menu at a given point.</summary>
    public event Action<PhysicalPoint>? ShowActionMenu;
    /// <summary>Raised to hide the action menu.</summary>
    public event Action? HideActionMenu;
    /// <summary>Raised to start manual (zoom-to-point) selection at a given point (captures a fresh screenshot).</summary>
    public event Action<PhysicalPoint>? StartManualSelection;
    /// <summary>Raised to re-show the manual-selection view when navigating BACK from the action menu,
    /// reusing the existing screenshot instead of re-capturing (the screen is dimmed by the backdrop).</summary>
    public event Action<PhysicalPoint>? RestoreManualSelectionView;
    /// <summary>Raised to end manual selection.</summary>
    public event Action? EndManualSelection;
    /// <summary>Raised to hide the manual selection overlay.</summary>
    public event Action? HideManualSelection;
    /// <summary>Fired when a Drag action is selected. Carries the resolved grab origin point.</summary>
    public event Action<PhysicalPoint>? DragStarted;

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

    /// <summary>Called when the current fixation ends (gaze moves away from the fixation point).</summary>
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

        if (action == InteractionAction.ScrollMode)
        {
            HideActionMenu?.Invoke();
            if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            ScrollAnchor = targetPoint;
            SetState(InteractionState.ScrollMode);
            return;
        }

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();

        if (action == InteractionAction.Drag)
        {
            SetState(InteractionState.Dragging);
            DragStarted?.Invoke(targetPoint.Value);
            return;
        }

        ActionRequested?.Invoke(action, targetPoint.Value);
        ResetToFreeLook();
    }

    /// <summary>Navigates back from the action menu to manual selection, or closes manual selection to free look.</summary>
    public void OnManualSelectionBack()
    {
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        {
            // Back from action view → return to ManualSelection selection. Reuse the existing
            // capture (do NOT re-capture — the backdrop is up, and the shot is unchanged).
            HideActionMenu?.Invoke();
            ManualSelectionDwellPoint = null;
            SetState(InteractionState.ManualSelectionActive);
            RestoreManualSelectionView?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ManualSelectionActive)
        {
            // Close from ManualSelection view → return to free look
            EndManualSelection?.Invoke();
            ResetToFreeLook();
        }
    }

    /// <summary>
    /// Dismisses the action menu and returns to free look.
    ///
    /// Covers the ManualSelection path too, since 2026-08-26. It guarded on
    /// <see cref="InteractionState.ActionMenuVisible"/> alone while nothing could dismiss the menu
    /// from the ManualSelection path — the ring's Back returns to the magnifier
    /// (<see cref="OnManualSelectionBack"/>), which is a step back rather than a way out. The
    /// right-edge Close is a way out from either state, so the magnified view has to be torn down
    /// as well; <see cref="TogglePause"/> does the same pairing.
    /// </summary>
    public void OnActionMenuDismissed()
    {
        if (CurrentState is not (InteractionState.ActionMenuVisible
                              or InteractionState.ManualSelectionActionMenuVisible)) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();
        ResetToFreeLook();
    }

    /// <summary>Toggles gaze tracking between paused and active states.</summary>
    /// <remarks>Blocked during <see cref="InteractionState.ScrollMode"/> and <see cref="InteractionState.Dragging"/> — use the stop/cancel buttons to exit those modes.</remarks>
    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else if (CurrentState is InteractionState.ScrollMode or InteractionState.Dragging)
        {
            return;
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

    /// <summary>Exits gaze-scroll mode and returns to free look.</summary>
    public void ExitScrollMode()
    {
        if (CurrentState != InteractionState.ScrollMode) return;
        ScrollAnchor = null;
        ResetToFreeLook();
    }

    /// <summary>Ends the drag operation and returns to FreeLook.</summary>
    public void CompleteDrag()
    {
        if (CurrentState != InteractionState.Dragging) return;
        ResetToFreeLook();
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ManualSelectionDwellPoint = null;
        AutoSelectionTarget = null;
        ScrollAnchor = null;
        SetState(InteractionState.FreeLook);
    }
}
