namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// GB3 (scroll-at-P, Part B): the worker's fast path. Within 8 physical px of the anchor the bare
/// wheel is sent exactly as before; farther away it sends one <c>ScrollAtAndRestore(anchor,
/// freshCursor, …)</c> batch per wheel tick, so the wheel lands at the anchor and the cursor is put
/// back. Ticks without a wheel send nothing. GB3 replaces Part A's GA4.
/// </summary>
public class ScrollWorkerTests
{
    private static readonly PhysicalPoint Anchor = new(1000, 1000);

    private static (ScrollWorker Worker, TestFakes.FakeInputSimulator Input) ArmedWorker(
        Func<PhysicalPoint?> cursorReader)
    {
        var input = new TestFakes.FakeInputSimulator();
        var worker = new ScrollWorker(input);
        worker.Start(Anchor, isArmed: () => true, cursorReader: cursorReader);
        worker.UpdateTargetVelocity(0, -600);   // a strong downward target: a wheel every tick
        return (worker, input);
    }

    [Fact]
    public void GB3_CursorAtTheAnchor_UsesTheBareWheel_AndNeverHops()
    {
        var (worker, input) = ArmedWorker(() => Anchor);
        using (worker)
        {
            for (int i = 0; i < 10; i++) worker.Tick();

            Check.WithCustomMessage("the premise: the ticks produced wheels").That(input.BareScrolls).Not.IsEmpty();
            Check.WithCustomMessage("a cursor on the anchor must not trigger a hop.")
                .That(input.RestoreScrolls).IsEmpty();
        }
    }

    /// <summary>
    /// The threshold boundary: ≤ 8 px is the bare path, 9 px needs the hop. The reader returns the
    /// point 8/9 px to the right of the anchor.
    /// </summary>
    [Theory]
    [InlineData(8.0, true)]
    [InlineData(9.0, false)]
    public void GB3_TheThresholdBoundaryIsEightPhysicalPixels(double distancePx, bool bare)
    {
        var cursor = new PhysicalPoint(Anchor.X + distancePx, Anchor.Y);
        var (worker, input) = ArmedWorker(() => cursor);
        using (worker)
        {
            for (int i = 0; i < 5; i++) worker.Tick();

            if (bare)
            {
                Check.WithCustomMessage("within the threshold the bare wheel must stay.").That(input.BareScrolls).Not.IsEmpty();
                Check.That(input.RestoreScrolls).IsEmpty();
            }
            else
            {
                Check.WithCustomMessage("past the threshold every wheel must hop.").That(input.RestoreScrolls).Not.IsEmpty();
                Check.That(input.BareScrolls).IsEmpty();
            }
        }
    }

    [Fact]
    public void GB3_AwayFromTheAnchor_HopsToTheAnchor_AndRestoresToThatCursor()
    {
        var cursor = new PhysicalPoint(1400, 1200);
        var (worker, input) = ArmedWorker(() => cursor);
        using (worker)
        {
            for (int i = 0; i < 10; i++) worker.Tick();

            Check.WithCustomMessage("the premise: the ticks produced wheels").That(input.RestoreScrolls).Not.IsEmpty();
            Check.WithCustomMessage("every hop targets the anchor and restores the live cursor.")
                .That(input.RestoreScrolls).ContainsOnlyElementsThatMatch(
                    s => s.At == Anchor && s.Restore == cursor && s.Dy < 0);
            Check.That(input.BareScrolls).IsEmpty();
            Check.That(input.PointedScrolls).IsEmpty();
        }
    }

    /// <summary>
    /// MB6: the restore point must be read on EVERY wheel tick, not once at Start. The first tick
    /// sees the anchor (bare); the external writer then moves the cursor, and the next tick must
    /// hop and restore to the NEW point.
    /// </summary>
    [Fact]
    public void GB3_ReadsTheCursorFreshOnEveryWheelTick()
    {
        var cursor = Anchor;
        var (worker, input) = ArmedWorker(() => cursor);
        using (worker)
        {
            worker.Tick();                       // cursor at the anchor: bare
            cursor = new PhysicalPoint(1400, 1000); // the external gaze mouse moved it
            worker.Tick();                       // must hop and restore to the NEW point

            Check.That(input.BareScrolls).HasSize(1);
            Check.That(input.RestoreScrolls).HasSize(1);
            Check.WithCustomMessage($"restored to {input.RestoreScrolls[0].Restore} instead of the live cursor.")
                .That(input.RestoreScrolls[0].Restore).IsEqualTo(new PhysicalPoint(1400, 1000));
            Check.That(input.RestoreScrolls[0].At).IsEqualTo(Anchor);
        }
    }

    /// <summary>
    /// F2: a FAILED cursor read (null) must keep the bare wheel. MBz's
    /// <c>else if (cursor is not null)</c> silently drops every wheel on a failed read; this row
    /// kills it (and nothing may throw on the way).
    /// </summary>
    [Fact]
    public void GB3_NullCursorRead_KeepsTheBareWheel()
    {
        var input = new TestFakes.FakeInputSimulator();
        using var worker = new ScrollWorker(input);
        worker.Start(Anchor, isArmed: () => true, cursorReader: () => null);
        worker.UpdateTargetVelocity(0, -600);

        for (int i = 0; i < 10; i++) worker.Tick();

        Check.WithCustomMessage("a failed cursor read must not drop the wheel.")
            .That(input.BareScrolls).Not.IsEmpty();
        Check.That(input.RestoreScrolls).IsEmpty();
    }

    [Fact]
    public void GB3_NoWheelTicks_SendNothing()
    {
        var input = new TestFakes.FakeInputSimulator();
        using var worker = new ScrollWorker(input);
        worker.Start(Anchor, isArmed: () => true, cursorReader: () => new PhysicalPoint(1400, 1000));

        for (int i = 0; i < 10; i++) worker.Tick();

        Check.That(input.BareScrolls).IsEmpty();
        Check.That(input.RestoreScrolls).IsEmpty();
        Check.That(input.PointedScrolls).IsEmpty();
        Check.That(input.Moves).IsEmpty();
    }
}
