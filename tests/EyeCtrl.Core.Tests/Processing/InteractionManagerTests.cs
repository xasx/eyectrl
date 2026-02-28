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
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        Assert.Equal(InteractionState.Fixation, manager.CurrentState);
    }

    [Fact]
    public void OnFixationEnded_DuringFixation_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnFixationEnded();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_ShowsActionMenu()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_WithZoom_TransitionsToZoom()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomActive, manager.CurrentState);
    }

    [Fact]
    public void OnActionSelected_FromMenu_FiresActionAndReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        InteractionAction? firedAction = null;
        ScreenPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Equal(InteractionAction.LeftClick, firedAction);
        Assert.Equal(new ScreenPoint(100, 100), firedPoint);
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
        ScreenPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomActive, manager.CurrentState);

        manager.OnZoomFixation(new ScreenPoint(110, 110));
        Assert.Equal(InteractionState.ZoomActionMenuVisible, manager.CurrentState);

        manager.OnActionSelected(InteractionAction.RightClick);
        Assert.Equal(InteractionAction.RightClick, firedAction);
        Assert.Equal(new ScreenPoint(110, 110), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void ZoomFlow_BackButton_ReturnsToFreeLook()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnZoomFixation(new ScreenPoint(110, 110));
        manager.OnZoomBack();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }
}
