namespace EyeCtrl.Core.Tests.Browse;

using EyeCtrl.Core.Browse;
using EyeCtrl.Core.Models;

/// <summary>
/// Contract of WebElementSetReconciler.Plan: a pure partition of the next snapshot
/// against the previous one — adds (new ids), re-adds (moved beyond the threshold: the
/// remove+add pair that discards accumulated dwell), rebases (small drift, updated in
/// place with state-preserving semantics), removes (gone ids). No id lands in two
/// buckets; an exactly unchanged element lands in none, so re-planning an unchanged
/// snapshot is a no-op.
/// </summary>
public class WebElementSetReconcilerTests
{
    private const double Threshold = 24;

    private static WebElementInfo El(string id, double x, double y, double width = 100, double height = 40)
        => new(id, WebElementKind.Button, new PhysicalRect(x, y, width, height));

    private static WebElementSnapshot Snap(int generation, params WebElementInfo[] elements)
        => new(generation, elements);

    [Fact]
    public void Plan_NullPrevious_AddsEverything()
    {
        var plan = WebElementSetReconciler.Plan(null, Snap(1, El("web_a", 10, 10), El("web_b", 200, 10)), Threshold);

        Check.That(plan.Adds).ContainsExactly(El("web_a", 10, 10), El("web_b", 200, 10));
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Rebases).IsEmpty();
        Check.That(plan.Removes).IsEmpty();
    }

    [Fact]
    public void Plan_NullPreviousWithEmptyNext_IsANoOp()
    {
        var plan = WebElementSetReconciler.Plan(null, WebElementSnapshot.Empty, Threshold);

        Check.That(plan.IsEmpty).IsTrue();
    }

    [Fact]
    public void Plan_UnchangedSnapshot_IsIdempotent()
    {
        var previous = Snap(3, El("web_a", 10, 10), El("web_b", 200, 500, 60, 30));
        var next = Snap(4, El("web_a", 10, 10), El("web_b", 200, 500, 60, 30));

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.IsEmpty).IsTrue();
        Check.That(plan.Adds).IsEmpty();
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Rebases).IsEmpty();
        Check.That(plan.Removes).IsEmpty();
    }

    [Fact]
    public void Plan_SmallDrift_RebasesInPlace_WithTheNextRect()
    {
        var previous = Snap(1, El("web_a", 100, 100));
        var next = Snap(2, El("web_a", 112, 100)); // 12 px ≤ threshold: drift, not a scroll

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Rebases).ContainsExactly(El("web_a", 112, 100));
        Check.That(plan.Adds).IsEmpty();
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Removes).IsEmpty();
    }

    [Fact]
    public void Plan_ResizeWithoutMove_Rebases_RatherThanResettingDwell()
    {
        var previous = Snap(1, El("web_a", 100, 100, 100, 40));
        var next = Snap(2, El("web_a", 100, 100, 320, 80)); // same top-left, grown

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Rebases).ContainsExactly(El("web_a", 100, 100, 320, 80));
        Check.That(plan.ReAdds).IsEmpty();
    }

    [Fact]
    public void Plan_MoveBeyondTheThreshold_IsAReAdd_TheRemoveAddPairThatResetsDwell()
    {
        // Page scroll: the element lands 400 px lower. The plan must say remove+add —
        // NOT a rebase, and NOT a plain add — so the caller discards accumulated dwell.
        var previous = Snap(1, El("web_a", 100, 100));
        var next = Snap(2, El("web_a", 100, 500));

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.ReAdds).ContainsExactly(El("web_a", 100, 500));
        Check.That(plan.Adds).IsEmpty();
        Check.That(plan.Rebases).IsEmpty();
        Check.That(plan.Removes).IsEmpty();
    }

    [Fact]
    public void Plan_MoveBeyondTheThresholdOnEitherAxis_AloneResets()
    {
        var previous = Snap(1, El("web_a", 100, 100));
        var next = Snap(2, El("web_a", 900, 100)); // dx 800, dy 0

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.ReAdds).ContainsExactly(El("web_a", 900, 100));
        Check.That(plan.Rebases).IsEmpty();
    }

    [Fact]
    public void Plan_MovementExactlyAtTheThreshold_Rebases()
    {
        // Strictly-greater resets: exactly-threshold displacement is still drift.
        var previous = Snap(1, El("web_a", 100, 100));
        var next = Snap(2, El("web_a", 124, 100));

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Rebases).ContainsExactly(El("web_a", 124, 100));
        Check.That(plan.ReAdds).IsEmpty();
    }

    [Fact]
    public void Plan_RemovedElement_LandsInRemoves_WithThePreviousRect()
    {
        var previous = Snap(1, El("web_a", 100, 100), El("web_gone", 300, 100));
        var next = Snap(2, El("web_a", 100, 100));

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Removes).ContainsExactly(El("web_gone", 300, 100));
        Check.That(plan.Adds).IsEmpty();
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Rebases).IsEmpty();
    }

    [Fact]
    public void Plan_EmptyNextSnapshot_RemovesEverything()
    {
        var previous = Snap(1, El("web_a", 10, 10), El("web_b", 200, 10), El("web_c", 400, 10));

        var plan = WebElementSetReconciler.Plan(previous, WebElementSnapshot.Empty, Threshold);

        Check.That(plan.Removes).ContainsExactly(El("web_a", 10, 10), El("web_b", 200, 10), El("web_c", 400, 10));
        Check.That(plan.Adds).IsEmpty();
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Rebases).IsEmpty();
    }

    [Fact]
    public void Plan_MixedChanges_PartitionIntoMutuallyExclusiveBuckets()
    {
        var previous = Snap(1,
            El("web_stay", 10, 10),
            El("web_drift", 10, 100),
            El("web_scrolled", 10, 200),
            El("web_gone", 10, 300));
        var next = Snap(2,
            El("web_stay", 10, 10),          // unchanged → no entry
            El("web_drift", 14, 100),        // 4 px → rebase
            El("web_scrolled", 10, 700),     // 500 px → re-add (dwell discarded)
            El("web_new", 10, 400));         // new id → add
                                             // web_gone vanished → remove

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Adds.Select(e => e.Id)).ContainsExactly("web_new");
        Check.That(plan.ReAdds.Select(e => e.Id)).ContainsExactly("web_scrolled");
        Check.That(plan.Rebases.Select(e => e.Id)).ContainsExactly("web_drift");
        Check.That(plan.Removes.Select(e => e.Id)).ContainsExactly("web_gone");

        // Buckets are mutually exclusive by id, and an unchanged id lands in none of them.
        var assigned = plan.Adds.Concat(plan.ReAdds).Concat(plan.Rebases).Concat(plan.Removes)
            .Select(e => e.Id).ToList();
        Check.That(assigned).HasSize(assigned.Distinct().Count());
        Check.That(assigned).Not.Contains("web_stay");
    }

    [Fact]
    public void Plan_NegativeThreshold_TreatsAnyMovementAsAReset()
    {
        var previous = Snap(1, El("web_a", 100, 100));
        var next = Snap(2, El("web_a", 101, 100));

        var plan = WebElementSetReconciler.Plan(previous, next, -5);

        Check.That(plan.ReAdds).ContainsExactly(El("web_a", 101, 100));
        Check.That(plan.Rebases).IsEmpty();
    }

    [Fact]
    public void Plan_DuplicateIdInNext_KeepsTheFirstOccurrence()
    {
        var previous = Snap(1, El("web_a", 10, 10));

        var plan = WebElementSetReconciler.Plan(previous, Snap(2, El("web_a", 20, 10), El("web_a", 900, 900)), Threshold);

        // First report wins for both the ordering and the geometry.
        Check.That(plan.Rebases).ContainsExactly(El("web_a", 20, 10));
        Check.That(plan.ReAdds).IsEmpty();
        Check.That(plan.Adds).IsEmpty();
    }

    [Fact]
    public void Plan_DuplicateIdInPrevious_KeepsTheFirstOccurrence()
    {
        var previous = Snap(1, El("web_a", 10, 10), El("web_a", 20, 10));
        var next = Snap(2, El("web_b", 10, 10));

        var plan = WebElementSetReconciler.Plan(previous, next, Threshold);

        Check.That(plan.Removes).ContainsExactly(El("web_a", 10, 10));
        Check.That(plan.Adds).ContainsExactly(El("web_b", 10, 10));
    }

    [Fact]
    public void Plan_ReplanningAnAppliedPlan_YieldsOnlyTheFreshlyReportedDrift()
    {
        // Idempotence through the full cycle: plan, apply by making the previous reflect
        // the plan's outcome, re-plan — nothing is left to do.
        var first = WebElementSetReconciler.Plan(
            Snap(1, El("web_a", 100, 100)), Snap(2, El("web_a", 110, 102), El("web_b", 300, 10)), Threshold);
        Check.That(first.Rebases).HasSize(1);
        Check.That(first.Adds).HasSize(1);

        var applied = Snap(3, El("web_a", 110, 102), El("web_b", 300, 10));
        var second = WebElementSetReconciler.Plan(applied, Snap(4, El("web_a", 110, 102), El("web_b", 300, 10)), Threshold);

        Check.That(second.IsEmpty).IsTrue();
    }
}
