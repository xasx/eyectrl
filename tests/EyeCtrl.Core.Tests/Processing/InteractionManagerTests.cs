namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManagerTests
{
    [Fact]
    public void InitialState_IsFreeLook()
    {
        var manager = new InteractionManager();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void OnFixationStarted_TransitionsToFixation()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    [Fact]
    public void OnFixationEnded_DuringFixation_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnFixationEnded();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void OnActivationButtonDwelled_ShowsActionMenu()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
    }

    [Fact]
    public void OnActivationButtonDwelled_WithManualSelection_TransitionsToManualSelection()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);
    }

    [Fact]
    public void OnActionSelected_FromMenu_FiresActionAndReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        InteractionAction? firedAction = null;
        PhysicalPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Check.That(firedAction).IsEqualTo(InteractionAction.LeftClick);
        Check.That(firedPoint).IsEqualTo(new PhysicalPoint(100, 100));
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void Pause_TransitionsToPaused()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    [Fact]
    public void Unpause_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        manager.TogglePause();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ManualSelectionFlow_FixateActivateFixateSelectAction()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        InteractionAction? firedAction = null;
        PhysicalPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        manager.OnActionSelected(InteractionAction.RightClick);
        Check.That(firedAction).IsEqualTo(InteractionAction.RightClick);
        Check.That(firedPoint).IsEqualTo(new PhysicalPoint(110, 110));
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ManualSelectionFlow_BackButton_TwoStepReturnsToFreeLook()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));

        // First back: action menu → ManualSelection selection
        manager.OnManualSelectionBack();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        // Second back: ManualSelection selection → free look
        manager.OnManualSelectionBack();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ManualSelectionBack_FromActionMenu_ReturnsToManualSelectionActive()
    {
        var im = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        var fixPoint = new PhysicalPoint(500, 500);
        var ManualSelectionPoint = new PhysicalPoint(510, 510);
        bool startManualSelectionFired = false;
        bool hideActionMenuFired = false;
        bool endManualSelectionFired = false;
        PhysicalPoint? restoredAt = null;

        im.StartManualSelection += _ => startManualSelectionFired = true;
        im.RestoreManualSelectionView += p => restoredAt = p;
        im.HideActionMenu += () => hideActionMenuFired = true;
        im.EndManualSelection += () => endManualSelectionFired = true;

        im.OnFixationStarted(fixPoint);
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        im.OnManualSelectionDwell(ManualSelectionPoint);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        // Back from action menu → returns to ManualSelection, NOT FreeLook.
        // It must RESTORE the existing view (reuse the screenshot), NOT re-start a fresh
        // capture: StartManualSelection must stay silent, RestoreManualSelectionView fires.
        startManualSelectionFired = false; // reset to detect an unwanted re-fire
        im.OnManualSelectionBack();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);
        Check.That(hideActionMenuFired).IsTrue();
        Check.That(startManualSelectionFired).IsFalse();
        Check.That(restoredAt).IsEqualTo(fixPoint);
        Check.That(endManualSelectionFired).IsFalse();
    }

    [Fact]
    public void OnManualSelectionDwell_FiresHideManualSelectionBeforeShowActionMenu()
    {
        var im = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        var events = new List<string>();

        im.HideManualSelection += () => events.Add("HideManualSelection");
        im.ShowActionMenu += _ => events.Add("ShowActionMenu");

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        im.OnManualSelectionDwell(new PhysicalPoint(110, 110));

        Check.That(events.TakeLast(2)).IsEqualTo(new[] { "HideManualSelection", "ShowActionMenu" });
    }

    [Fact]
    public void ManualSelectionClose_FromManualSelectionView_ReturnsToFreeLook()
    {
        var im = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        var fixPoint = new PhysicalPoint(500, 500);
        bool endManualSelectionFired = false;

        im.EndManualSelection += () => endManualSelectionFired = true;

        im.OnFixationStarted(fixPoint);
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        im.OnManualSelectionBack(); // close from ManualSelection view → FreeLook
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(endManualSelectionFired).IsTrue();
    }

    [Fact]
    public void DefaultSelectionMode_IsNone()
    {
        var manager = new InteractionManager();
        Check.That(manager.SelectionMode).IsEqualTo(SelectionMode.None);
    }

    [Fact]
    public void OnActivationButtonDwelled_AutoMode_WithTarget_ShowsActionMenuAtControlCenter()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
        PhysicalPoint? menuPoint = null;
        manager.ShowActionMenu += p => menuPoint = p;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
        manager.OnActivationButtonDwelled();

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.That(menuPoint).IsEqualTo(new PhysicalPoint(175, 175));
    }

    [Fact]
    public void OnActivationButtonDwelled_AutoMode_NoTarget_FallsBackToFixationPoint()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
        PhysicalPoint? menuPoint = null;
        manager.ShowActionMenu += p => menuPoint = p;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = null;
        manager.OnActivationButtonDwelled();

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        Check.That(menuPoint).IsEqualTo(new PhysicalPoint(100, 100));
    }

    [Fact]
    public void OnActionSelected_AutoMode_ReturnsStoredTargetPoint()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
        PhysicalPoint? firedPoint = null;
        manager.ActionRequested += (_, p) => firedPoint = p;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Check.That(firedPoint).IsEqualTo(new PhysicalPoint(175, 175));
    }

    [Fact]
    public void OnActionSelected_AutoMode_NoTarget_FallsBackToFixationPoint()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
        PhysicalPoint? firedPoint = null;
        manager.ActionRequested += (_, p) => firedPoint = p;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = null;
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Check.That(firedPoint).IsEqualTo(new PhysicalPoint(100, 100));
    }

    [Fact]
    public void ResetToFreeLook_ClearsAutoSelectionTarget()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(0, 0, 10, 10));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(manager.AutoSelectionTarget).IsNull();
    }

    [Fact]
    public void OnActionSelected_Drag_FromActionMenu_TransitionsToDragging()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        manager.OnActionSelected(InteractionAction.Drag);

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);
    }

    [Fact]
    public void OnActionSelected_Drag_FromManualSelectionActionMenu_TransitionsToDragging()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        bool endManualSelectionFired = false;
        manager.EndManualSelection += () => endManualSelectionFired = true;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        manager.OnActionSelected(InteractionAction.Drag);

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(endManualSelectionFired).IsTrue();
    }

    [Fact]
    public void OnActionSelected_Drag_DoesNotFireActionRequested()
    {
        var manager = new InteractionManager();
        bool actionRequestedFired = false;
        manager.ActionRequested += (_, _) => actionRequestedFired = true;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.Drag);

        Check.That(actionRequestedFired).IsFalse();
    }

    [Fact]
    public void OnActionSelected_Drag_FiresDragStarted_WithResolvedOrigin()
    {
        var manager = new InteractionManager();
        PhysicalPoint? dragOrigin = null;
        manager.DragStarted += (origin) => dragOrigin = origin;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.Drag);

        Check.That(dragOrigin).IsEqualTo(new PhysicalPoint(100, 100));
    }

    [Fact]
    public void OnActionSelected_Drag_AutoMode_ResolvesControlCenter()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
        PhysicalPoint? dragOrigin = null;
        manager.DragStarted += (origin) => dragOrigin = origin;

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.Drag);

        Check.That(dragOrigin).IsEqualTo(new PhysicalPoint(175, 175));
    }

    [Fact]
    public void CompleteDrag_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.Drag);
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);

        manager.CompleteDrag();

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TogglePause_DuringDragging_IsBlocked()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.Drag);
        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);

        manager.TogglePause();

        Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);
    }
}
