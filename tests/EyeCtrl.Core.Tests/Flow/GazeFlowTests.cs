namespace EyeCtrl.Core.Tests.Flow;

using System.Linq;
using EyeCtrl.Core.Models;

/// <summary>
/// End-to-end interaction-flow tests driven entirely by synthetic gaze through the real
/// GazeEngine + InteractionManager. Fully headless: no windows, no gaze hardware, and no real
/// cursor/input (the harness records input instead), so these are safe to run without disturbing
/// an eye-tracking user and complete in milliseconds.
///
/// The gaze→fixation edge is exercised for real (through <see cref="EyeCtrl.Core.Processing.FixationDetector"/>);
/// the activation-button and menu-button dwells live in the ViewModel layer, so they are simulated
/// here via the InteractionManager callbacks the ViewModels would fire.
/// </summary>
public class GazeFlowTests
{
    [Fact]
    public void HoldingGaze_TriggersFixation_AndFreezesCursor()
    {
        var h = new GazeFlowHarness();

        h.Hold(500, 400, durationMs: 450); // longer than FixationDetectionMs (300ms default)

        Check.That(h.State).IsEqualTo(InteractionState.Fixation);
        Check.That(h.States[0]).IsEqualTo(InteractionState.Fixation); // FreeLook → Fixation was the first transition
        Check.That(h.Onsets.Count).IsEqualTo(1);
        Check.That(h.Onsets[0].X).IsEqualTo(500d);
        Check.That(h.Onsets[0].Y).IsEqualTo(400d);

        // The cursor moved while free-looking, then must FREEZE once fixating — the core behavior
        // that keeps an eye-tracking user from losing their pointer while aiming.
        var movesWhenFixated = h.Input.MoveCount;
        Check.That(movesWhenFixated).IsStrictlyGreaterThan(0);

        h.Hold(500, 400, durationMs: 300); // keep looking at the same spot
        Check.That(h.Input.MoveCount).IsEqualTo(movesWhenFixated); // no further cursor movement
    }

    [Fact]
    public void ActionMenuMode_FixationToMenuToClick()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.Hold(600, 500, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Interaction.OnActivationButtonDwelled(); // ViewModel dwell would fire this
        Check.That(h.State).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.That(h.ActionMenusShown.Count).IsEqualTo(1);
        Check.That(h.ActionMenusShown[0].X).IsEqualTo(600d);

        h.Interaction.OnActionSelected(InteractionAction.LeftClick); // picking a menu button
        Check.That(h.ActionsRequested.Select(a => a.Action)).Contains(InteractionAction.LeftClick);
    }

    [Fact]
    public void ManualSelectionMode_FixationToManualSelectionToActionMenu()
    {
        var h = new GazeFlowHarness(SelectionMode.ManualSelection);

        h.Hold(700, 300, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Interaction.OnActivationButtonDwelled();
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActive);
        Check.That(h.ManualSelectionsStarted.Count).IsEqualTo(1);

        var precise = new PhysicalPoint(720, 315);
        h.Interaction.OnManualSelectionDwell(precise); // precise dwell inside the magnifier
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);
        Check.That(h.ActionMenusShown.Count).IsEqualTo(1);
        Check.That(h.ActionMenusShown[0].X).IsEqualTo(720d); // menu opens at the precise point
    }

    [Fact]
    public void LosingFixation_ReturnsToFreeLook()
    {
        var h = new GazeFlowHarness();

        h.Hold(500, 400, durationMs: 450);
        Check.That(h.State).IsEqualTo(InteractionState.Fixation);

        h.Hold(1400, 1000, durationMs: 100); // gaze jumps far away, briefly (not long enough to re-fixate)
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void SelectingScrollMode_EntersScrollMode_ThenExitReturnsToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(600, 500);
        h.Interaction.OnActionSelected(InteractionAction.ScrollMode);

        Check.That(h.State).IsEqualTo(InteractionState.ScrollMode);
        Check.That(h.Interaction.ScrollAnchor.HasValue).IsTrue();

        h.Interaction.ExitScrollMode();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
        Check.That(h.Interaction.ScrollAnchor.HasValue).IsFalse();
    }

    [Fact]
    public void SelectingDrag_EntersDragging_ThenCompleteReturnsToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.Drag);

        Check.That(h.State).IsEqualTo(InteractionState.Dragging);
        Check.That(h.DragsStarted.Count).IsEqualTo(1); // grab origin handed to the drag overlay

        h.Interaction.CompleteDrag();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RepeatMode_CursorKeepsFollowingGaze()
    {
        var h = new GazeFlowHarness(SelectionMode.None);

        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.LeftClick);
        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);

        // Unlike fixating (which freezes the pointer while aiming), repeat mode keeps the cursor
        // glued to gaze — the user aims the repeating click BY looking, and must see where it lands.
        var movesBefore = h.Input.MoveCount;
        h.Gaze(900, 700);
        Check.That(h.Input.MoveCount).IsStrictlyGreaterThan(movesBefore);
    }

    [Fact]
    public void RepeatMode_GazeLost_AutoExitsAfterTwoSeconds()
    {
        // Review PR #139 finding 1 core: every filtered view of lost eyes looks valid, so the
        // exit reads RAW validity (NoteGazeSample). The user cannot dwell the stop button or the
        // menu while their eyes are away — the mode must end itself.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);
        h.Interaction.EnterRepeatMode(InteractionAction.LeftClick);

        h.HoldInvalid(640, 480, durationMs: 2500);
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RepeatMode_HeldGaze_FiresTheLockedActionAtTheFixatedPoint()
    {
        // End-to-end dwell-to-act (D23-D25): fixation arms the dwell, valid samples advance it,
        // and the locked action fires AT the fixated point after RepeatDwellMs.
        var h = new GazeFlowHarness(SelectionMode.None);
        h.Interaction.RepeatDwellMs = 400;
        h.FixateAndActivate(640, 480);
        h.Interaction.OnActionSelected(InteractionAction.LeftClick);  // back to FreeLook
        h.Interaction.EnterRepeatMode(InteractionAction.RightClick);
        var baseline = h.ActionsRequested.Count;

        h.Hold(800, 600, durationMs: 1100);   // 300 fixation + 400 dwell + margin

        Check.That(h.State).IsEqualTo(InteractionState.RepeatMode);   // stays locked
        var fired = h.ActionsRequested.Skip(baseline).ToList();
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.RightClick);
        Check.That(fired[0].Point.X).IsCloseTo(800, 60);
        Check.That(fired[0].Point.Y).IsCloseTo(600, 60);
    }

    [Fact]
    public void Pause_SuppressesCursorMovement_AndTogglesBack()
    {
        var h = new GazeFlowHarness();

        h.Interaction.TogglePause();
        Check.That(h.State).IsEqualTo(InteractionState.Paused);

        var movesBefore = h.Input.MoveCount;
        h.Hold(800, 600, durationMs: 300); // gaze while paused must NOT move the cursor or fixate
        Check.That(h.Input.MoveCount).IsEqualTo(movesBefore);
        Check.That(h.State).IsEqualTo(InteractionState.Paused);

        h.Interaction.TogglePause();
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ManualSelectionBack_StepsMenuToMagnifier_ThenMagnifierToFreeLook()
    {
        var h = new GazeFlowHarness(SelectionMode.ManualSelection);

        h.FixateAndActivate(700, 300);
        h.Interaction.OnManualSelectionDwell(new PhysicalPoint(710, 305));
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        h.Interaction.OnManualSelectionBack(); // back from action menu → magnifier
        Check.That(h.State).IsEqualTo(InteractionState.ManualSelectionActive);

        h.Interaction.OnManualSelectionBack(); // back again → close to free look
        Check.That(h.State).IsEqualTo(InteractionState.FreeLook);
    }
}
