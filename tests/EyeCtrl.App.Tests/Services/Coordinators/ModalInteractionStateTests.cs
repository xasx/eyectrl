namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using Xunit;

/// <summary>
/// C9's <see cref="ModalInteractionState.ProbeOpen"/> raise contract: per subscriber through
/// <c>GetInvocationList()</c>, each in its own try/catch. A coordinator whose recompute throws must
/// not stop the remaining gates from observing the flip — a half-armed probe is the failure this
/// exists to prevent.
/// </summary>
public class ModalInteractionStateTests
{
    [Fact]
    public void ProbeOpen_AssignmentRaisesPerSubscriber_AndAThrowerDoesNotStopTheRest()
    {
        var modal = new ModalInteractionState();
        int first = 0, follower = 0;
        modal.ProbeOpenChanged += _ => { first++; throw new InvalidOperationException("subscriber bug"); };
        modal.ProbeOpenChanged += _ => follower++;

        modal.ProbeOpen = true;

        Check.That(first).IsEqualTo(1);
        Check.WithCustomMessage("a throwing subscriber must not stop the remaining subscribers")
            .That(follower).IsEqualTo(1);
        Check.That(modal.ProbeOpen).IsTrue();
    }

    [Fact]
    public void ProbeOpen_NoChangeAssignment_DoesNotRaise()
    {
        var modal = new ModalInteractionState();
        int raised = 0;
        modal.ProbeOpenChanged += _ => raised++;

        modal.ProbeOpen = false;   // already false — no edge
        Check.That(raised).IsEqualTo(0);

        modal.ProbeOpen = true;
        modal.ProbeOpen = true;    // second assignment is not an edge
        Check.That(raised).IsEqualTo(1);
    }
}
