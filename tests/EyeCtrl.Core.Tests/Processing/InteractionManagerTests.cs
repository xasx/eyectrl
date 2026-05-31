namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManagerTests
{
    [Fact]
    public void InitialState_IsFreeLook()
    {
        var manager = new InteractionManager();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void OnFixationStarted_TransitionsToFixation()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        Assert.Equal(InteractionState.Fixation, manager.CurrentState);
    }

    [Fact]
    public void OnFixationEnded_DuringFixation_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnFixationEnded();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_ShowsActionMenu()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_WithManualSelection_TransitionsToManualSelection()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ManualSelectionActive, manager.CurrentState);
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

        Assert.Equal(InteractionAction.LeftClick, firedAction);
        Assert.Equal(new PhysicalPoint(100, 100), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void Pause_TransitionsToPaused()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        Assert.Equal(InteractionState.Paused, manager.CurrentState);
    }

    [Fact]
    public void Unpause_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        manager.TogglePause();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
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
        Assert.Equal(InteractionState.ManualSelectionActive, manager.CurrentState);

        manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));
        Assert.Equal(InteractionState.ManualSelectionActionMenuVisible, manager.CurrentState);

        manager.OnActionSelected(InteractionAction.RightClick);
        Assert.Equal(InteractionAction.RightClick, firedAction);
        Assert.Equal(new PhysicalPoint(110, 110), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
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
        Assert.Equal(InteractionState.ManualSelectionActive, manager.CurrentState);

        // Second back: ManualSelection selection → free look
        manager.OnManualSelectionBack();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
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

        im.StartManualSelection += _ => startManualSelectionFired = true;
        im.HideActionMenu += () => hideActionMenuFired = true;
        im.EndManualSelection += () => endManualSelectionFired = true;

        im.OnFixationStarted(fixPoint);
        im.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ManualSelectionActive, im.CurrentState);

        im.OnManualSelectionDwell(ManualSelectionPoint);
        Assert.Equal(InteractionState.ManualSelectionActionMenuVisible, im.CurrentState);

        // Back from action menu → returns to ManualSelection, NOT FreeLook
        startManualSelectionFired = false; // reset to detect re-fire
        im.OnManualSelectionBack();
        Assert.Equal(InteractionState.ManualSelectionActive, im.CurrentState);
        Assert.True(hideActionMenuFired, "Should fire HideActionMenu");
        Assert.True(startManualSelectionFired, "Should re-fire StartManualSelection to re-show ManualSelection overlay");
        Assert.False(endManualSelectionFired, "Should NOT fire EndManualSelection");
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

        Assert.Equal(new[] { "HideManualSelection", "ShowActionMenu" }, events.TakeLast(2));
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
        Assert.Equal(InteractionState.ManualSelectionActive, im.CurrentState);

        im.OnManualSelectionBack(); // close from ManualSelection view → FreeLook
        Assert.Equal(InteractionState.FreeLook, im.CurrentState);
        Assert.True(endManualSelectionFired);
    }

    [Fact]
    public void DefaultSelectionMode_IsNone()
    {
        var manager = new InteractionManager();
        Assert.Equal(SelectionMode.None, manager.SelectionMode);
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

        Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
        Assert.Equal(new PhysicalPoint(175, 175), menuPoint);
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

        Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
        Assert.Equal(new PhysicalPoint(100, 100), menuPoint);
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

        Assert.Equal(new PhysicalPoint(175, 175), firedPoint);
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

        Assert.Equal(new PhysicalPoint(100, 100), firedPoint);
    }

    [Fact]
    public void ResetToFreeLook_ClearsAutoSelectionTarget()
    {
        var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(0, 0, 10, 10));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
        Assert.Null(manager.AutoSelectionTarget);
    }
}
