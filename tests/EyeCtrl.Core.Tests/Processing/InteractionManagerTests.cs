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
    public void OnActivationButtonDwelled_WithZoom_TransitionsToZoom()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomSelectionActive, manager.CurrentState);
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
    public void ZoomFlow_FixateActivateFixateSelectAction()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        InteractionAction? firedAction = null;
        PhysicalPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomSelectionActive, manager.CurrentState);

        manager.OnZoomFixation(new PhysicalPoint(110, 110));
        Assert.Equal(InteractionState.ZoomSelectionActionMenuVisible, manager.CurrentState);

        manager.OnActionSelected(InteractionAction.RightClick);
        Assert.Equal(InteractionAction.RightClick, firedAction);
        Assert.Equal(new PhysicalPoint(110, 110), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void ZoomFlow_BackButton_TwoStepReturnsToFreeLook()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        manager.OnFixationStarted(new PhysicalPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnZoomFixation(new PhysicalPoint(110, 110));

        // First back: action menu → zoom selection
        manager.OnZoomBack();
        Assert.Equal(InteractionState.ZoomSelectionActive, manager.CurrentState);

        // Second back: zoom selection → free look
        manager.OnZoomBack();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void ZoomBack_FromActionMenu_ReturnsToZoomSelectionActive()
    {
        var im = new InteractionManager { ZoomEnabled = true };
        var fixPoint = new PhysicalPoint(500, 500);
        var zoomPoint = new PhysicalPoint(510, 510);
        bool startZoomFired = false;
        bool hideActionMenuFired = false;
        bool endZoomFired = false;

        im.StartZoom += _ => startZoomFired = true;
        im.HideActionMenu += () => hideActionMenuFired = true;
        im.EndZoom += () => endZoomFired = true;

        im.OnFixationStarted(fixPoint);
        im.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);

        im.OnZoomFixation(zoomPoint);
        Assert.Equal(InteractionState.ZoomSelectionActionMenuVisible, im.CurrentState);

        // Back from action menu → returns to zoom, NOT FreeLook
        startZoomFired = false; // reset to detect re-fire
        im.OnZoomBack();
        Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);
        Assert.True(hideActionMenuFired, "Should fire HideActionMenu");
        Assert.True(startZoomFired, "Should re-fire StartZoom to re-show zoom overlay");
        Assert.False(endZoomFired, "Should NOT fire EndZoom");
    }

    [Fact]
    public void OnZoomFixation_FiresHideZoomBeforeShowActionMenu()
    {
        var im = new InteractionManager { ZoomEnabled = true };
        var events = new List<string>();

        im.HideZoom += () => events.Add("HideZoom");
        im.ShowActionMenu += _ => events.Add("ShowActionMenu");

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        im.OnZoomFixation(new PhysicalPoint(110, 110));

        Assert.Equal(new[] { "HideZoom", "ShowActionMenu" }, events.TakeLast(2));
    }

    [Fact]
    public void ZoomClose_FromZoomView_ReturnsToFreeLook()
    {
        var im = new InteractionManager { ZoomEnabled = true };
        var fixPoint = new PhysicalPoint(500, 500);
        bool endZoomFired = false;

        im.EndZoom += () => endZoomFired = true;

        im.OnFixationStarted(fixPoint);
        im.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);

        im.OnZoomBack(); // close from zoom view → FreeLook
        Assert.Equal(InteractionState.FreeLook, im.CurrentState);
        Assert.True(endZoomFired);
    }
}
