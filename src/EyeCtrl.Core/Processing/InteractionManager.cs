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
    /// <summary>
    /// The last click-family action dispatched through <see cref="OnActionSelected"/> (left,
    /// right, double, middle). Null until the first such click. Feeds both
    /// <see cref="InteractionAction.RepeatLastAction"/> and the action locked on entering
    /// <see cref="InteractionState.RepeatMode"/>.
    /// </summary>
    public InteractionAction? LastRepeatableAction { get; private set; }
    /// <summary>The action locked for the active repeat session, or null outside <see cref="InteractionState.RepeatMode"/>.</summary>
    public InteractionAction? RepeatAction { get; private set; }

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

    // Repeat-mode liveness gates (review PR #139 finding 1). StableCentroid never goes null when
    // the tracker loses the eyes — past the interpolation window every filtered view of a
    // lost-eyes gap is a valid-looking frozen point — so liveness is latched here from the RAW
    // sample validity GazeEngine forwards in OnGazePoint.
    private const long LostGazeSkipThresholdUs = 750_000;   // brief losses (blinks) still tick
    private const long LostGazeExitThresholdUs = 2_000_000; // sustained loss ends the mode
    private long _lastSampleTimestampUs = long.MinValue;
    private long _lastValidGazeTimestampUs = long.MinValue;
    private long _lastTickObservedSampleUs = long.MinValue;

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

        if (action == InteractionAction.RepeatMode)
        {
            HideActionMenu?.Invoke();
            if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            RepeatAction = LastRepeatableAction ?? InteractionAction.LeftClick;
            SetState(InteractionState.RepeatMode);
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

        if (action == InteractionAction.RepeatLastAction)
            action = LastRepeatableAction ?? InteractionAction.LeftClick;

        if (action.IsRepeatableClick())
            LastRepeatableAction = action;

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
    /// <remarks>Blocked during <see cref="InteractionState.ScrollMode"/>, <see cref="InteractionState.Dragging"/> and <see cref="InteractionState.RepeatMode"/> — use the stop/cancel affordances to exit those modes.</remarks>
    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else if (CurrentState is InteractionState.ScrollMode or InteractionState.Dragging or InteractionState.RepeatMode)
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

    /// <summary>Exits repeat mode and returns to free look.</summary>
    public void ExitRepeatMode()
    {
        if (CurrentState != InteractionState.RepeatMode) return;
        ResetToFreeLook();
    }

    /// <summary>
    /// Latches raw gaze-stream liveness: called by <see cref="GazeEngine"/> for EVERY sample,
    /// before calibration or gap interpolation — every downstream view of a lost-eyes gap looks
    /// like a valid frozen point, so this is the only spot where "eyes actually present" is
    /// still observable. Also ends repeat mode once lost gaze outlasts
    /// <see cref="LostGazeExitThresholdUs"/>: the user cannot dwell the stop button while their
    /// eyes are away, so the mode must not keep itself (or its clicks) alive.
    /// </summary>
    public void NoteGazeSample(bool isValid, long timestampUs)
    {
        if (timestampUs > _lastSampleTimestampUs) _lastSampleTimestampUs = timestampUs;
        if (isValid && timestampUs > _lastValidGazeTimestampUs) _lastValidGazeTimestampUs = timestampUs;

        if (CurrentState == InteractionState.RepeatMode
            && _lastSampleTimestampUs > _lastValidGazeTimestampUs
            && _lastSampleTimestampUs - _lastValidGazeTimestampUs > LostGazeExitThresholdUs)
            ExitRepeatMode();
    }

    /// <summary>
    /// Fires one repeat tick at the given live gaze point. Called by the app-side repeat
    /// controller's timer; guarded so a tick that lands after exit (or before entry) is a no-op
    /// rather than a stray click, and so no tick fires when the gaze behind the point is stale:
    /// a valid-looking frozen centroid is exactly what a lost or dead tracker produces.
    /// </summary>
    public void FireRepeatTick(PhysicalPoint point)
    {
        if (CurrentState != InteractionState.RepeatMode || RepeatAction is not { } action) return;

        var validityAgeUs = _lastSampleTimestampUs - _lastValidGazeTimestampUs;
        if (validityAgeUs > LostGazeSkipThresholdUs) return;

        // Dead-stream guard: with no samples at all, validity age cannot grow, so a tick also
        // requires that a sample arrived since the previous tick. The long.MinValue sentinel
        // (no sample ever seen — pure state-machine callers) allows the tick instead.
        if (_lastTickObservedSampleUs != long.MinValue
            && _lastSampleTimestampUs <= _lastTickObservedSampleUs) return;
        _lastTickObservedSampleUs = _lastSampleTimestampUs;

        ActionRequested?.Invoke(action, point);
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ManualSelectionDwellPoint = null;
        AutoSelectionTarget = null;
        ScrollAnchor = null;
        RepeatAction = null;
        SetState(InteractionState.FreeLook);
    }
}
