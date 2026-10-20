namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// Part A gates for <see cref="ScrollWorker"/>. GA4 (scroll mode untouched) is superseded by GB3
/// once Part B lands; in Part A alone the worker must keep sending BARE wheels and must never move
/// the cursor — the scroll-start rule "no per-tick MOVE to the anchor".
/// </summary>
public class ScrollWorkerTests
{
    [StaFact]
    public void GA4_TicksSendBareWheelsOnly_AndNeverMoveTheCursor()
    {
        var input = new TestFakes.FakeInputSimulator();
        using var worker = new ScrollWorker(input);
        worker.Start(
            new PhysicalPoint(1000, 1000),
            isArmed: () => true,
            cursorReader: () => new PhysicalPoint(1400, 1000)); // far away: still the bare path in A
        worker.UpdateTargetVelocity(0, -600);

        for (int i = 0; i < 20; i++) worker.Tick();

        Check.WithCustomMessage("the premise: the ticks produced wheels").That(input.BareScrolls).Not.IsEmpty();
        Check.WithCustomMessage("Part A must not route scroll mode through the pointed one-shot.")
            .That(input.PointedScrolls).IsEmpty();
        Check.WithCustomMessage("the worker must never move the cursor.")
            .That(input.Moves).IsEmpty();
    }
}
