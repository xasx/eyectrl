namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class InteractionManagerRepeatModeTests
{
    private static InteractionManager InActionMenu(out PhysicalPoint fixation)
    {
        var im = new InteractionManager();
        fixation = new PhysicalPoint(1000, 800);
        im.OnFixationStarted(fixation);
        im.OnActivationButtonDwelled();
        return im;
    }

    [Fact]
    public void SelectingRepeatMode_EntersRepeatMode_WithoutFiringActionRequested()
    {
        var im = InActionMenu(out _);
        var fired = new List<InteractionAction>();
        im.ActionRequested += (a, _) => fired.Add(a);

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(fired).IsEmpty();
    }

    [Fact]
    public void SelectingRepeatMode_WithClickHistory_LocksThatClick()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RightClick);   // history + back to FreeLook
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.RightClick);
    }

    [Fact]
    public void SelectingRepeatMode_WithNoHistory_LocksLeftClick()
    {
        var im = InActionMenu(out _);

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void ClickFamilyDispatch_UpdatesLastRepeatableAction_NonClickDoesNot()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.MiddleClick);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);

        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.PinNote);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);
    }

    [Fact]
    public void SelectingRepeatLastAction_FiresTheRememberedClickAtTheTarget()
    {
        var im = InActionMenu(out var fixation);
        im.OnActionSelected(InteractionAction.RightClick);   // history
        im.OnFixationStarted(fixation);
        im.OnActivationButtonDwelled();

        InteractionAction? firedAction = null;
        PhysicalPoint? firedPoint = null;
        im.ActionRequested += (a, p) => { firedAction = a; firedPoint = p; };

        im.OnActionSelected(InteractionAction.RepeatLastAction);

        Check.That(firedAction).IsEqualTo(InteractionAction.RightClick);
        Check.That(firedPoint!.Value.X).IsCloseTo(fixation.X, 1e-6);
        Check.That(firedPoint!.Value.Y).IsCloseTo(fixation.Y, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void SelectingRepeatLastAction_WithNoHistory_FiresLeftClick()
    {
        var im = InActionMenu(out _);
        InteractionAction? firedAction = null;
        im.ActionRequested += (a, _) => firedAction = a;

        im.OnActionSelected(InteractionAction.RepeatLastAction);

        Check.That(firedAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void FireRepeatTick_InRepeatMode_FiresTheLockedAction_AndStaysInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.DoubleClick);
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.RepeatMode);

        var fired = new List<(InteractionAction Action, PhysicalPoint Point)>();
        im.ActionRequested += (a, p) => fired.Add((a, p));

        im.FireRepeatTick(new PhysicalPoint(120, 130));
        im.FireRepeatTick(new PhysicalPoint(140, 150));

        Check.That(fired).HasSize(2);
        Check.That(fired[0].Action).IsEqualTo(InteractionAction.DoubleClick);
        Check.That(fired[0].Point.X).IsCloseTo(120, 1e-6);
        Check.That(fired[1].Point.Y).IsCloseTo(150, 1e-6);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void FireRepeatTick_OutsideRepeatMode_IsANoOp()
    {
        var im = new InteractionManager();
        var fired = false;
        im.ActionRequested += (_, _) => fired = true;

        im.FireRepeatTick(new PhysicalPoint(10, 10));

        Check.That(fired).IsFalse();
    }

    [Fact]
    public void ExitRepeatMode_ReturnsToFreeLook_AndClearsTheLockedAction()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.ExitRepeatMode();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.RepeatAction).IsNull();
    }

    [Fact]
    public void ExitRepeatMode_OutsideRepeatMode_IsANoOp()
    {
        var im = new InteractionManager();
        im.ExitRepeatMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TogglePause_IsBlockedInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.TogglePause();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void FixationStarted_IsIgnoredInRepeatMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);

        im.OnFixationStarted(new PhysicalPoint(500, 500));

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void EnteringRepeatMode_FiresStateChangedWithRepeatMode()
    {
        var im = InActionMenu(out _);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.OnActionSelected(InteractionAction.RepeatMode);

        Check.That(captured).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void ExitingRepeatMode_FiresStateChangedWithFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.RepeatMode);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.ExitRepeatMode();

        Check.That(captured).IsEqualTo(InteractionState.FreeLook);
    }
}
