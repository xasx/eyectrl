namespace EyeCtrl.Core.Tests.ActionMenu;

using System.Collections.Generic;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Pins for the plan's fit geometry and its two bounds: the legibility floor (~36 DIP buttons)
/// and the scale cap. The budgets are the reference displays the settings shell is tested
/// against — 688 DIP at 1920x1080, 494 at 1080p/125%, 256 on the tested small display — so a
/// constant that moves silently reddens here instead of in a settings page nobody re-measured.
///
/// The pins follow the adopted scale-to-fit shape (2026-09-30): the preview is present, scaled,
/// where the scalar stays above the floor; absent at 256, where it does not.
/// </summary>
public class RingPlanScaleTests
{
    private const double Budget1080p = 688.0;
    private const double Budget1080p125 = 494.0;
    private const double BudgetSmall = 256.0;

    /// <summary>Wider than any ring, so the height dimension is the one that binds.</summary>
    private const double UnboundedWidth = 4000.0;

    private static RingPlan DefaultPlan() => RingPlan.Create(
        new List<InteractionAction> { InteractionAction.LeftClick },
        new List<InteractionAction> { InteractionAction.LeftClick },
        new CursorSettings().CircleSize);

    [Fact]
    public void WindowSize_AtTheDefaultCircleSize_IsPinned()
    {
        // The scale pins below are only meaningful while this holds:
        // 250 -> radius 250/2 + 70 + 60 = 255 -> window (255 + 70) * 2 + 20 = 670.
        var plan = DefaultPlan();

        Check.That(new CursorSettings().CircleSize).IsEqualTo(250.0);
        Check.That(plan.EffectiveRingRadius).IsEqualTo(255.0);
        Check.That(plan.WindowSize).IsEqualTo(670.0);
    }

    [Theory]
    [InlineData(Budget1080p, 1.0)]
    [InlineData(Budget1080p125, 494.0 / 670.0)]
    [InlineData(BudgetSmall, 256.0 / 670.0)]
    public void Scale_AtTheReferenceBudgets_IsPinned(double budget, double expectedScale)
    {
        Check.That(DefaultPlan().ScaleFor(UnboundedWidth, budget)).IsCloseTo(expectedScale, 1e-12);
    }

    [Fact]
    public void Scale_AtTheReferenceBudgets_TurnsTheLegibilityPinsIntoPresence()
    {
        var plan = DefaultPlan();

        // 1080p holds the true ring; 125% shrinks it but stays above the floor.
        Check.That(RingPlan.IsLegibleAt(plan.ScaleFor(UnboundedWidth, Budget1080p))).IsTrue();
        Check.That(RingPlan.IsLegibleAt(plan.ScaleFor(UnboundedWidth, Budget1080p125))).IsTrue();

        // The small display's scalar drops the button under the floor: absent beats approximate.
        var small = plan.ScaleFor(UnboundedWidth, BudgetSmall);
        Check.That(RingPlan.IsLegibleAt(small)).IsFalse();
        Check.That(LayoutConstants.ActionMenuButtonRadius * small)
             .IsStrictlyLessThan(RingPlan.LegibilityFloorButtonRadiusDip);
    }

    [Fact]
    public void LegibilityAndFitBounds_ArePinned()
    {
        Check.That(RingPlan.LegibilityFloorButtonRadiusDip).IsEqualTo(36.0);
        Check.That(RingPlan.MinimumLegibleScale).IsCloseTo(36.0 / 70.0, 1e-12);
        Check.That(RingPlan.ScaleCap).IsEqualTo(1.0);
        Check.That(RingPlan.WindowSizeMargin).IsEqualTo(20.0);
    }

    [Fact]
    public void Scale_NeverMagnifies()
    {
        var plan = DefaultPlan();

        Check.That(plan.ScaleFor(100_000, 100_000)).IsEqualTo(RingPlan.ScaleCap);
        Check.That(plan.ScaleFor(UnboundedWidth, Budget1080p)).IsEqualTo(1.0);
    }

    [Fact]
    public void Scale_IsBoundByTheNarrowerDimension()
    {
        var plan = DefaultPlan();

        // 335 DIP is half the 670 window: half scale even with height to spare, either way round.
        Check.That(plan.ScaleFor(335.0, UnboundedWidth)).IsCloseTo(0.5, 1e-12);
        Check.That(plan.ScaleFor(UnboundedWidth, 335.0)).IsCloseTo(0.5, 1e-12);
    }

    [Fact]
    public void ScaledWindowSize_FitsTheBudgetItWasComputedFor()
    {
        var plan = DefaultPlan();

        foreach (var budget in new[] { Budget1080p, Budget1080p125, BudgetSmall })
            Check.WithCustomMessage($"budget {budget}")
                 .That(plan.WindowSize * plan.ScaleFor(UnboundedWidth, budget)).IsLessOrEqualThan(budget);
    }

    [Fact]
    public void UnscaledRing_FitsTheReferenceBudget_UpToCircleSize268()
    {
        // WindowSize = CircleSize + 420, so the true ring fits 688 DIP up to CircleSize 268 —
        // the boundary the final synthesis records; beyond it the preview shrinks.
        var at268 = RingPlan.Create(null, null, 268.0);
        Check.That(at268.WindowSize).IsEqualTo(688.0);
        Check.That(at268.ScaleFor(UnboundedWidth, Budget1080p)).IsEqualTo(1.0);

        var beyond = RingPlan.Create(null, null, 300.0);
        Check.That(beyond.WindowSize).IsEqualTo(720.0);
        Check.That(beyond.ScaleFor(UnboundedWidth, Budget1080p)).IsCloseTo(688.0 / 720.0, 1e-12);
        Check.That(RingPlan.IsLegibleAt(beyond.ScaleFor(UnboundedWidth, Budget1080p))).IsTrue();
    }

    [Fact]
    public void LegibilityFloor_IsInclusive()
    {
        Check.That(RingPlan.IsLegibleAt(RingPlan.MinimumLegibleScale)).IsTrue();
        Check.That(RingPlan.IsLegibleAt(RingPlan.MinimumLegibleScale - 1e-6)).IsFalse();
    }
}
