namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class InteractionManagerRepeatModeTests
{
    private static InteractionManager InFreeLookWithHistory(InteractionAction seeded = InteractionAction.RightClick)
    {
        var im = new InteractionManager();
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(seeded);   // seeds LastRepeatableAction and returns to FreeLook
        return im;
    }

    [Fact]
    public void EnterRepeatMode_FromFreeLook_EntersRepeatMode_WithoutFiringActionRequested()
    {
        var im = new InteractionManager();
        var fired = new List<InteractionAction>();
        im.ActionRequested += (a, _) => fired.Add(a);

        im.EnterRepeatMode();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(fired).IsEmpty();
    }

    [Fact]
    public void EnterRepeatMode_IsIgnoredOutsideFreeLook()
    {
        // Modes are mutually exclusive and the entry must not trample a live surface: the
        // toolbar button is dwellable in every state, so the guard is load-bearing.
        var im = new InteractionManager();
        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();   // ActionMenuVisible
        im.EnterRepeatMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        var im2 = new InteractionManager();
        im2.TogglePause();                 // Paused
        im2.EnterRepeatMode();
        Check.That(im2.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    [Fact]
    public void SelectingRepeatMode_WithClickHistory_LocksThatClick()
    {
        var im = InFreeLookWithHistory(InteractionAction.RightClick);

        im.EnterRepeatMode();

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.RightClick);
    }

    [Fact]
    public void SelectingRepeatMode_WithNoHistory_LocksLeftClick()
    {
        var im = new InteractionManager();

        im.EnterRepeatMode();

        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void ClickFamilyDispatch_UpdatesLastRepeatableAction_NonClickDoesNot()
    {
        var im = InFreeLookWithHistory(InteractionAction.MiddleClick);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);

        im.OnFixationStarted(new PhysicalPoint(1000, 800));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.PinNote);
        Check.That(im.LastRepeatableAction).IsEqualTo(InteractionAction.MiddleClick);
    }

    [Fact]
    public void FireRepeatTick_InRepeatMode_FiresTheLockedAction_AndStaysInRepeatMode()
    {
        var im = InFreeLookWithHistory(InteractionAction.DoubleClick);
        im.EnterRepeatMode();

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
        var im = InFreeLookWithHistory();
        im.EnterRepeatMode();

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
        var im = InFreeLookWithHistory();
        im.EnterRepeatMode();

        im.TogglePause();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void FixationStarted_IsIgnoredInRepeatMode()
    {
        var im = InFreeLookWithHistory();
        im.EnterRepeatMode();

        im.OnFixationStarted(new PhysicalPoint(500, 500));

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void EnteringRepeatMode_FiresStateChangedWithRepeatMode()
    {
        var im = InFreeLookWithHistory();
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.EnterRepeatMode();

        Check.That(captured).IsEqualTo(InteractionState.RepeatMode);
    }

    [Fact]
    public void ExitingRepeatMode_FiresStateChangedWithFreeLook()
    {
        var im = InFreeLookWithHistory();
        im.EnterRepeatMode();
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;

        im.ExitRepeatMode();

        Check.That(captured).IsEqualTo(InteractionState.FreeLook);
    }
}
