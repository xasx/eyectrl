namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class InteractionManagerScrollModeTests
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
    public void SelectingScrollMode_EntersScrollModeWithFixationAnchor()
    {
        var im = InActionMenu(out var fixation);
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
        Check.That(im.ScrollAnchor).IsNotNull();
        Check.That(im.ScrollAnchor!.Value.X).IsCloseTo(fixation.X, 1e-6);
        Check.That(im.ScrollAnchor!.Value.Y).IsCloseTo(fixation.Y, 1e-6);
    }

    [Fact]
    public void SelectingScrollMode_DoesNotFireActionRequested()
    {
        var im = InActionMenu(out _);
        bool fired = false;
        im.ActionRequested += (_, _) => fired = true;
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(fired).IsFalse();
    }

    [Fact]
    public void ExitScrollMode_ReturnsToFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        im.ExitScrollMode();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.ScrollAnchor).IsNull();
    }

    [Fact]
    public void FixationStarted_IsIgnoredInScrollMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        im.OnFixationStarted(new PhysicalPoint(500, 500));
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
    }

    [Fact]
    public void EnteringScrollMode_FiresStateChangedWithScrollMode()
    {
        var im = InActionMenu(out _);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(captured).IsNotNull();
        Check.That(captured!.Value).IsEqualTo(InteractionState.ScrollMode);
    }

    [Fact]
    public void ExitScrollMode_FiresStateChangedWithFreeLook()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        InteractionState? captured = null;
        im.StateChanged += s => captured = s;
        im.ExitScrollMode();
        Check.That(captured).IsNotNull();
        Check.That(captured!.Value).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TogglePause_DoesNotAffectScrollMode()
    {
        var im = InActionMenu(out _);
        im.OnActionSelected(InteractionAction.ScrollMode);
        im.TogglePause();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
        Check.That(im.ScrollAnchor).IsNotNull();
    }
}
