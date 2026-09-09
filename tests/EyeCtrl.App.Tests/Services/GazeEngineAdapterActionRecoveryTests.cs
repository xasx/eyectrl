namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using Xunit;

/// <summary>
/// The recovery contract for a throwing action handler: whatever state
/// <c>OnActionSelected</c> died in, the user must end up back in <c>FreeLook</c>.
/// <para>
/// <b>Why this exists.</b> <c>GazeEngineAdapter</c> guards the <c>ActionSelectedMessage</c>
/// handler — that path is the entry point to a drag, and an escaping throw reaches the dispatcher
/// where the app deliberately does not set <c>Handled</c>, killing the process with the physical
/// mouse button possibly still pressed. The guard's RESET was originally copied from the activation
/// handler above it, which calls <c>OnFixationEnded()</c>. That call is guarded on
/// <c>CurrentState == Fixation</c>, and <c>OnActionSelected</c> is never in <c>Fixation</c> when it
/// throws: it enters at <c>ActionMenuVisible</c> and may already have moved to <c>Dragging</c> or
/// <c>ScrollMode</c>. So the recovery looked symmetric and recovered nothing.
/// </para>
/// <para>
/// Being stuck in <c>Dragging</c> or <c>ScrollMode</c> is the worst outcome available:
/// <c>TogglePause</c> is deliberately blocked in both, so a gaze-only user would have no cancel
/// button and no way to pause out of it.
/// </para>
/// <para>
/// These assert the property directly on <see cref="InteractionManager"/> rather than through the
/// adapter, because the adapter's constructor needs a live <c>GazeEngine</c> and dispatcher while
/// the thing worth pinning is which reset calls actually recover from which states. The adapter
/// calls exactly this trio.
/// </para>
/// </summary>
public class GazeEngineAdapterActionRecoveryTests
{
    /// <summary>The reset the adapter's catch performs. All three are state-guarded no-ops.</summary>
    private static void ResetAfterActionError(InteractionManager im)
    {
        im.CompleteDrag();
        im.ExitScrollMode();
        im.OnActionMenuDismissed();
    }

    private static InteractionManager InActionMenu()
    {
        var im = new InteractionManager();
        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);
        return im;
    }

    [Fact]
    public void TheOldReset_RecoversFromNoneOfTheReachableStates()
    {
        // Pins the defect itself, so a future "simplification" back to OnFixationEnded fails here
        // rather than shipping. Each of these is a state OnActionSelected can throw in.
        var menu = InActionMenu();
        menu.OnFixationEnded();
        Check.That(menu.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        var dragging = InActionMenu();
        dragging.OnActionSelected(InteractionAction.Drag);
        Check.That(dragging.CurrentState).IsEqualTo(InteractionState.Dragging);
        dragging.OnFixationEnded();
        Check.That(dragging.CurrentState).IsEqualTo(InteractionState.Dragging);

        var scrolling = InActionMenu();
        scrolling.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(scrolling.CurrentState).IsEqualTo(InteractionState.ScrollMode);
        scrolling.OnFixationEnded();
        Check.That(scrolling.CurrentState).IsEqualTo(InteractionState.ScrollMode);
    }

    [Fact]
    public void TheReset_RecoversFromActionMenuVisible()
    {
        var im = InActionMenu();

        ResetAfterActionError(im);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TheReset_RecoversFromDragging()
    {
        // The worst case: TogglePause is blocked in Dragging, so being stuck here leaves a
        // gaze-only user with no cancel button and no way to pause out.
        var im = InActionMenu();
        im.OnActionSelected(InteractionAction.Drag);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);

        ResetAfterActionError(im);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void TheReset_RecoversFromScrollMode()
    {
        var im = InActionMenu();
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);

        ResetAfterActionError(im);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.ScrollAnchor).IsNull();
    }

    [Fact]
    public void TheReset_IsHarmlessFromFreeLook()
    {
        // The trio runs unconditionally in the catch, so it must be safe when the throw happened
        // somewhere that left the state already clean.
        var im = new InteractionManager();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);

        Check.ThatCode(() => ResetAfterActionError(im)).DoesNotThrow();

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void AThrowingActionSubscriber_LeavesTheStateRecoverable()
    {
        // End to end on the real failure shape: a subscriber throws out of OnActionSelected, the
        // state is left mid-action, and the adapter's reset must still return the user to FreeLook.
        var im = InActionMenu();
        im.ActionRequested += (_, _) => throw new InvalidOperationException("handler blew up");

        Check.ThatCode(() => im.OnActionSelected(InteractionAction.LeftClick))
            .Throws<InvalidOperationException>();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        ResetAfterActionError(im);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }
}
