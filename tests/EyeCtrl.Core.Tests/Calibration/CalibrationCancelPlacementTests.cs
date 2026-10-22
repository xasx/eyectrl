namespace EyeCtrl.Core.Tests.Calibration;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

/// <summary>
/// The calibration Cancel target's placement: the point inside the radius-inset display that is
/// farthest from every calibration target. The acceptance contract is geometric — every target
/// stays at least the hit radius plus a gap away, for every shipped layout — so these are pure
/// numeric tests, no window, no DPI.
/// </summary>
public class CalibrationCancelPlacementTests
{
    private static readonly PhysicalPoint HighDpiScreen = new(3840, 2400);
    private static readonly PhysicalPoint StandardScreen = new(1920, 1080);

    /// <summary>The hit radius the window actually computes at the owner's 200 % (72 DIP × 2).</summary>
    private const double OwnerRadiusPx = CalibrationCancelPlacement.HitRadiusDip * 2;

    public static TheoryData<PhysicalPoint> Screens() => new() { HighDpiScreen, StandardScreen };

    [Theory]
    [MemberData(nameof(Screens))]
    public void ForEveryLayout_ThePointIsInsideTheRadiusInset_AndKeepsTheRadiusPlusAGapFromEveryTarget(
        PhysicalPoint screen)
    {
        foreach (var radius in new[] { CalibrationCancelPlacement.HitRadiusDip, OwnerRadiusPx })
        {
            for (var count = 3; count <= 16; count++)
            {
                var targets = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var point = CalibrationCancelPlacement.GetPosition(targets, screen, radius);

                Check.WithCustomMessage($"{screen} count={count} radius={radius}: left edge")
                    .That(point.X).IsGreaterOrEqualThan(radius - 1e-6);
                Check.WithCustomMessage($"{screen} count={count} radius={radius}: right edge")
                    .That(point.X).IsLessOrEqualThan(screen.X - radius + 1e-6);
                Check.WithCustomMessage($"{screen} count={count} radius={radius}: top edge")
                    .That(point.Y).IsGreaterOrEqualThan(radius - 1e-6);
                Check.WithCustomMessage($"{screen} count={count} radius={radius}: bottom edge")
                    .That(point.Y).IsLessOrEqualThan(screen.Y - radius + 1e-6);

                var nearest = targets.Min(t => Distance(point, t));
                Check.WithCustomMessage(
                        $"{screen} count={count} radius={radius}: the nearest target is {nearest:0.#} px away, "
                        + $"needs at least radius + gap = {radius + CalibrationCancelPlacement.MinimumTargetGapPx:0.#}")
                    .That(nearest)
                    .IsGreaterOrEqualThan(radius + CalibrationCancelPlacement.MinimumTargetGapPx - 1e-6);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void ThePoint_IsAtLeastAsFarFromTheTargets_AsEveryOtherInsetPointOnACoarseGrid(
        PhysicalPoint screen)
    {
        const double radius = OwnerRadiusPx;
        for (var count = 3; count <= 16; count++)
        {
            var targets = CalibrationTargetPositionHelper.GetPositions(count, screen);
            var point = CalibrationCancelPlacement.GetPosition(targets, screen, radius);
            var ours = targets.Min(t => Distance(point, t));

            // The rule is "farthest from every target", not just "far enough": a sampled sweep of
            // the whole inset rectangle must not find a better point.
            const int samples = 24;
            var insetW = screen.X - (2 * radius);
            var insetH = screen.Y - (2 * radius);
            for (var j = 0; j <= samples; j++)
            {
                for (var i = 0; i <= samples; i++)
                {
                    var x = radius + (insetW * i / samples);
                    var y = radius + (insetH * j / samples);
                    var other = targets.Min(t => Distance(x, y, t));
                    Check.WithCustomMessage(
                            $"{screen} count={count}: ({x:0.#}, {y:0.#}) is {other:0.#} px from its nearest target, "
                            + $"farther than the chosen point's {ours:0.#}")
                        .That(other).IsLessOrEqualThan(ours + 1e-6);
                }
            }
        }
    }

    [Fact]
    public void FivePointLayout_OnTheOwnersDisplay_BeatsTheMidEdgePoint()
    {
        // The ticket's example is a mid-edge point ("e.g. top-centre"). The true maximin on this
        // layout sits on the vertical centre line between the corner column and the centre target,
        // where the distance to both is balanced — measurably farther from the nearest target than
        // top-centre, which is what the rule asks for.
        var targets = CalibrationTargetPositionHelper.GetPositions(5, HighDpiScreen);
        var point = CalibrationCancelPlacement.GetPosition(targets, HighDpiScreen, OwnerRadiusPx);

        Check.WithCustomMessage("the point must sit on the horizontal centre line")
            .That(Math.Abs(point.Y - (HighDpiScreen.Y / 2))).IsLessOrEqualThan(2);
        Check.WithCustomMessage("the point must stay clear of the corner column")
            .That(point.X).IsStrictlyGreaterThan(CalibrationTargetPositionHelper.MarginPx(HighDpiScreen) + OwnerRadiusPx);

        var ours = targets.Min(t => Distance(point, t));
        var topCentre = new PhysicalPoint(HighDpiScreen.X / 2, OwnerRadiusPx);
        var midEdge = targets.Min(t => Distance(topCentre, t));
        Check.WithCustomMessage(
                $"the chosen point keeps {ours:0.#} px from its nearest target; top-centre keeps {midEdge:0.#}")
            .That(ours).IsStrictlyGreaterThan(midEdge);
    }

    [Fact]
    public void WithNoTargets_ReturnsTheInsetCentre()
    {
        var point = CalibrationCancelPlacement.GetPosition(
            Array.Empty<PhysicalPoint>(), StandardScreen, 144);

        Check.That(point.X).IsEqualTo(StandardScreen.X / 2);
        Check.That(point.Y).IsEqualTo(StandardScreen.Y / 2);
    }

    [Fact]
    public void OnADisplayTooSmallForTheInsetBox_ReturnsTheCentre()
    {
        var tiny = new PhysicalPoint(320, 240);
        var point = CalibrationCancelPlacement.GetPosition(
            [new PhysicalPoint(48, 48), new PhysicalPoint(272, 48), new PhysicalPoint(160, 192)], tiny, 200);

        Check.That(point).IsEqualTo(new PhysicalPoint(160, 120));
    }

    [Fact]
    public void TheResult_IsDeterministic()
    {
        var targets = CalibrationTargetPositionHelper.GetPositions(9, HighDpiScreen);
        var first = CalibrationCancelPlacement.GetPosition(targets, HighDpiScreen, OwnerRadiusPx);
        var second = CalibrationCancelPlacement.GetPosition(targets, HighDpiScreen, OwnerRadiusPx);

        Check.That(second).IsEqualTo(first);
    }

    [Fact]
    public void ANegativeRadius_IsRejected()
    {
        ArgumentOutOfRangeException? error = null;
        try
        {
            CalibrationCancelPlacement.GetPosition(
                CalibrationTargetPositionHelper.GetPositions(5, StandardScreen), StandardScreen, -1);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            error = ex;
        }

        Check.That(error).IsNotNull();
        Check.That(error!.ParamName).IsEqualTo("radiusPx");
    }

    [Fact]
    public void NullTargets_AreRejected()
    {
        ArgumentNullException? error = null;
        try
        {
            CalibrationCancelPlacement.GetPosition(null!, StandardScreen, 72);
        }
        catch (ArgumentNullException ex)
        {
            error = ex;
        }

        Check.That(error).IsNotNull();
    }

    private static double Distance(PhysicalPoint a, PhysicalPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Distance(double x, double y, PhysicalPoint b) =>
        Math.Sqrt(((x - b.X) * (x - b.X)) + ((y - b.Y) * (y - b.Y)));
}
