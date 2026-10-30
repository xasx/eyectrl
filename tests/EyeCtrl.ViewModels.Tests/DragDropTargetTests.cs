namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C2: the drag's gaze-following drop target. It used to be an r20 circle at cursor − (14, 14) DIP
/// that reset its dwell on every re-centre, so the cursor sat 0.2 DIP inside its down-right edge
/// and any steady drift that way made a drop impossible — and in Manual Selection that dwell is
/// the only way to drop. Now: radius max(r50 DIP, fixation radius) capped at 100 DIP, centred on
/// the gaze, re-centred without a reset while the gaze stays within 1.5 radii of the dwell's spot.
///
/// <para>
/// Every scenario runs in Manual Selection (the settle never drops there, so the release dwell is
/// the only drop path being measured), at 30 Hz, with the owner's fixation radius of 80 px and
/// the shipped 500 ms pause grace unless a row says otherwise, at DPI 1 and 2.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragDropTargetTests
{
    private const long StepUs = 33_333; // 30 Hz
    private const int DwellMs = 600;    // DragReleaseButtonDwellMs default
    private const double OwnerFixationRadiusPx = 80;

    // ── radius ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 80.0, 80.0)]    // the owner at 100 %: the fixation radius beats r50
    [InlineData(2.0, 80.0, 100.0)]   // the owner at 200 %: r50 DIP is 100 px
    [InlineData(1.0, 10.0, 50.0)]    // a tight fixation radius never shrinks it below r50
    [InlineData(2.0, 10.0, 100.0)]
    [InlineData(1.0, 150.0, 100.0)]  // capped at 100 DIP
    [InlineData(1.0, 200.0, 100.0)]
    [InlineData(2.0, 200.0, 200.0)]  // 100 DIP at 200 % is the setting's own maximum
    public void TheHitRadius_IsAtLeastR50AndTheFixationRadius_CappedAt100Dip(
        double scale, double fixationPx, double expectedPx)
    {
        WithDpi(scale, () =>
        {
            using var world = new World(s => s.Dwell.FixationRadiusPixels = fixationPx);

            Check.WithCustomMessage($"DPI {scale}, fixation {fixationPx} px.")
                .That(world.Trail.ReleaseRadiusPhysical).IsCloseTo(expectedPx, 1e-9);
            Check.WithCustomMessage("the detector's hit radius is not the radius the disc is drawn at.")
                .That(world.DetectorRadius).IsCloseTo(world.Trail.ReleaseRadiusPhysical, 1e-9);
            Check.That(world.Trail.ReleaseDisc.RadiusPhysical).IsEqualTo(world.Trail.ReleaseRadiusPhysical);
        });
    }

    [Fact]
    public void TheEdgeBarsDragButtonRadius_IsUnchanged()
    {
        // The old drop target shared DragButtonRadius with the edge bar's buttons; C2 gave the
        // drop target its own constants, so this one must stay where the edge bar needs it.
        Check.That(UIConstants.DragButtonRadius).IsEqualTo(20.0);
        Check.That(UIConstants.DragDropTargetMinRadius).IsEqualTo(UIConstants.ButtonRadius);
    }

    // ── placement ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheTarget_IsCentredOnTheGaze(double scale)
    {
        WithDpi(scale, () =>
        {
            using var world = new World(arm: false);
            Check.That(world.Trail.ReleaseButtonCenter).IsEqualTo(World.Grab);

            var p = new PhysicalPoint(900, 700);
            world.Send(p, 0);

            Check.WithCustomMessage("the target did not re-centre ON the gaze after a saccade.")
                .That(world.Trail.ReleaseButtonCenter).IsEqualTo(p);
        });
    }

    // ── drift ─────────────────────────────────────────────────────────────────

    public static TheoryData<double, double, int, int> DriftRows()
    {
        var rows = new TheoryData<double, double, int, int>();
        foreach (var scale in new[] { 1.0, 2.0 })
            foreach (var speed in new[] { 0.05, 0.2, 0.5 })
                for (var direction = 0; direction < 8; direction++)
                    rows.Add(scale, speed, direction, 500);
        // Grace 0: no edge is crossed within one dwell, so the radius alone carries a slow drift.
        rows.Add(1.0, 0.5, 1, 0);
        rows.Add(2.0, 0.5, 1, 0);
        return rows;
    }

    [Theory]
    [MemberData(nameof(DriftRows))]
    public void ASteadyDrift_CompletesTheDropWithinTheDwellPlusTwoSamples(
        double scale, double dipPerSample, int direction, int graceMs)
    {
        // The defect: at r20 with the (−14, −14) offset, a drift with a down or right component
        // above ~0.01 DIP/sample left the circle within a sample and every re-centre reset it.
        WithDpi(scale, () =>
        {
            using var world = new World(s => s.Dwell.DwellPauseGraceMs = graceMs);
            var angle = direction * Math.PI / 4;
            var step = DpiHelper.LogicalToPhysicalX(dipPerSample);
            var start = new PhysicalPoint(900, 700);

            long? drop = null;
            for (var i = 0; i < 90 && drop is null; i++)
            {
                var at = new PhysicalPoint(
                    start.X + (i * step * Math.Cos(angle)), start.Y + (i * step * Math.Sin(angle)));
                world.Send(at, i * StepUs);
                if (world.Drops > 0) drop = i * StepUs;
            }

            // Sample 0 is the saccade that places the target; the dwell starts on sample 1.
            var limit = StepUs + (DwellMs * 1000L) + (2 * StepUs);
            Check.WithCustomMessage(
                    $"DPI {scale}, {dipPerSample} DIP/sample at {direction * 45}°, grace {graceMs}: "
                    + $"no drop within the dwell plus two samples (drop at {drop?.ToString() ?? "never"} µs).")
                .That(drop is not null && drop <= limit).IsTrue();
        });
    }

    // ── repeated edge crossings within the same spot ──────────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void RepeatedEdgeCrossingsWithinTheSpot_CompleteAtGrace500_AndNeverCreditTimeOutside(double scale)
    {
        // The owner's wander: a slow drift plus small re-fixations that carry the gaze across the
        // target's edge again and again while staying within 1.5 radii of where the dwell began.
        // Fixup-01 R2: the re-fixations are 0.6 R moves, under a radius per 3-sample window, so
        // none of them is a saccade (they were 1.2 R one-sample jumps, which R2 now restarts).
        // Each crossing re-centres the target WITHOUT a reset; the crossing sample itself is
        // outside the circle as drawn, so the pause grace holds it and it is never credited.
        // Mutants: the reset on every re-centre (never completes) and a re-centre before the hit
        // test (credits the crossing samples, so it completes at exactly the dwell).
        WithDpi(scale, () =>
        {
            using var world = new World(s => s.Dwell.DwellPauseGraceMs = 500);
            var r = world.Trail.ReleaseRadiusPhysical;
            var result = world.DriveCrossingWander(r, maxSamples: 120);

            Check.WithCustomMessage(
                    $"DPI {scale}: repeated crossings within the spot never dropped "
                    + $"({result.Crossings} crossings over {result.Samples} samples).")
                .That(world.Drops).IsEqualTo(1);
            Check.WithCustomMessage("the pattern must actually cross the edge several times.")
                .That(result.CrossingsBeforeDrop).IsStrictlyGreaterThan(2);

            // Never credited: every crossing costs at least one sample interval of the dwell.
            var earliest = (DwellMs * 1000L) + (result.CrossingsBeforeDrop * StepUs);
            Check.WithCustomMessage(
                    $"DPI {scale}: dropped after {result.DwellUs} µs of dwell with "
                    + $"{result.CrossingsBeforeDrop} crossings — time outside was credited.")
                .That(result.DwellUs).IsGreaterOrEqualThan(earliest - 1);
            Check.WithCustomMessage($"DPI {scale}: the drop took {result.DwellUs} µs, over twice the dwell.")
                .That(result.DwellUs).IsLessOrEqualThan(2 * DwellMs * 1000L);

            // And the drop lands within the spot, never somewhere the wander did not rest.
            Check.That(world.DropPoint!.Value.DistanceTo(result.Spot))
                .IsLessOrEqualThan(DragTrailViewModel.SameSpotRadii * r);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void RepeatedEdgeCrossings_AtGrace0_ResetOnEachCrossing(double scale)
    {
        // Grace 0 is reset-on-leave on every surface, and this one is no exception: each crossing
        // sample is outside the circle as drawn, so the dwell restarts there. With the crossings
        // closer together than a dwell, the same wander never drops.
        WithDpi(scale, () =>
        {
            using var world = new World(s => s.Dwell.DwellPauseGraceMs = 0);
            var result = world.DriveCrossingWander(world.Trail.ReleaseRadiusPhysical, maxSamples: 120);

            Check.That(result.Crossings).IsStrictlyGreaterThan(10);
            Check.WithCustomMessage($"DPI {scale}: grace 0 credited across a crossing.")
                .That(world.Drops).IsEqualTo(0);
        });
    }

    // ── leaving the spot ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AStaircaseWalkingAway_NeverDropsMidWalk_AndDropsWhereItStops(double scale)
    {
        // Each step is 1.2 radii — inside 1.5 radii of the CURRENT centre, but the steps add up.
        // The dwell belongs to the spot it started at, so once the walk is 1.5 radii from that
        // spot the dwell restarts (PX2/R4). Since fixup-01 R2 each 1.2 R one-sample step is
        // itself a saccade and restarts the dwell, so this row now pins that a walk of saccades
        // never drops mid-walk; the same-spot bound's own pin is the steady-glide row below.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var start = new PhysicalPoint(300, 300);
            var t = 0L;
            for (var step = 0; step < 12; step++)
            {
                var at = new PhysicalPoint(start.X + (step * 1.2 * r * 0.6), start.Y + (step * 1.2 * r * 0.8));
                for (var k = 0; k < 6; k++, t += StepUs)
                    world.Send(at, t);
            }

            Check.WithCustomMessage($"DPI {scale}: the dwell rode a 12-step walk and dropped mid-walk.")
                .That(world.Drops).IsEqualTo(0);

            var last = new PhysicalPoint(start.X + (11 * 1.2 * r * 0.6), start.Y + (11 * 1.2 * r * 0.8));
            for (var k = 0; k < 20; k++, t += StepUs)
                world.Send(last, t);

            Check.That(world.Drops).IsEqualTo(1);
            // The centroid of identical samples (fixup-01 R3), up to the sum's rounding.
            Check.That(world.DropPoint!.Value.DistanceTo(last)).IsLessOrEqualThan(1e-6);
        });
    }

    // ── wander ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(1.0, 2)]
    [InlineData(2.0, 3)]
    [InlineData(2.0, 4)]
    public void ARandomWalkInsideTheRadius_Completes(double scale, int seed)
    {
        // The owner's hold is a random walk, not a point. Steps of up to 0.15 radii, reflected so
        // the walk stays inside 0.9 radii of where it began: it never leaves the circle, so the
        // drop takes exactly the dwell.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var rng = new Random(seed);
            var spot = new PhysicalPoint(800, 600);
            var at = spot;
            world.Send(spot, 0);
            long? drop = null;
            for (var i = 1; i < 60 && drop is null; i++)
            {
                var next = new PhysicalPoint(
                    at.X + ((rng.NextDouble() - 0.5) * 0.3 * r), at.Y + ((rng.NextDouble() - 0.5) * 0.3 * r));
                if (next.DistanceTo(spot) <= 0.9 * r)
                    at = next;
                world.Send(at, i * StepUs);
                if (world.Drops > 0) drop = i * StepUs;
            }

            Check.That(drop is not null && drop <= StepUs + (DwellMs * 1000L) + StepUs).IsTrue();
        });
    }

    // ── progress ──────────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseProgress_FollowsTheDwell_AndTheGraceHoldShowsAsPaused()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(800, 600);
            for (var i = 0; i <= 10; i++)
                world.Send(spot, i * StepUs);

            // 10 credited intervals of 18: the saccade's landing sample is inside the target that
            // R2 placed on it, so the dwell starts there.
            Check.That(world.Trail.ReleaseProgress).IsCloseTo(10 * StepUs / (DwellMs * 1000.0), 1e-6);
            Check.That(world.Trail.IsReleasePaused).IsFalse();

            // Out towards the edge in 0.3 R steps — under a radius per 3-sample window, so not a
            // saccade (fixup-01 R2; this was one 1.2 R jump, which R2 now restarts).
            for (var k = 1; k <= 3; k++)
                world.Send(new PhysicalPoint(spot.X + (k * 0.3 * r), spot.Y), (10 + k) * StepUs);
            var held = world.Trail.ReleaseProgress;

            // A crossing: outside the drawn circle, inside the spot — held, not reset.
            var across = new PhysicalPoint(spot.X + (1.2 * r), spot.Y);
            world.Send(across, 14 * StepUs);
            Check.That(world.Trail.IsReleasePaused).IsTrue();
            Check.That(world.Trail.ReleaseProgress).IsCloseTo(held, 1e-9);
            Check.That(world.Trail.ReleaseDisc.Paused).IsTrue();

            world.Send(across, 15 * StepUs);
            Check.That(world.Trail.IsReleasePaused).IsFalse();
            world.Send(across, 16 * StepUs);
            Check.That(world.Trail.ReleaseProgress).IsStrictlyGreaterThan(held);
            Check.That(world.Trail.ReleaseDisc.Progress).IsEqualTo(world.Trail.ReleaseProgress);
        });
    }

    [Fact]
    public void ADrop_ClearsTheReleaseProgress()
    {
        WithDpi(1.0, () =>
        {
            using var world = new World();
            var spot = new PhysicalPoint(800, 600);
            for (var i = 0; i <= 25 && world.Drops == 0; i++)
                world.Send(spot, i * StepUs);

            Check.That(world.Drops).IsEqualTo(1);
            Check.That(world.Trail.ReleaseProgress).IsEqualTo(0);
        });
    }

    // ── fixup-01 R1: the leave-before-arm latch ───────────────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ALingerThatWandersUnderOneAndAHalfRadii_FromTheFirstSample_NeverArms(double scale)
    {
        // The drag starts with the gaze on the hidden Drag menu item. The gaze creeps 1.25 R from
        // that first sample (0.25 R a sample, never a saccade) and rests there for 2 s: still the
        // same rest, so nothing may drop. Mutant: the latch armed at 1.0 R drops it there.
        WithDpi(scale, () =>
        {
            using var world = new World(arm: false);
            var r = world.Trail.ReleaseRadiusPhysical;
            var menu = new PhysicalPoint(1300, 1000);
            var t = 0L;
            for (var k = 0; k <= 5; k++, t += StepUs)
                world.Send(new PhysicalPoint(menu.X + (k * 0.25 * r), menu.Y), t);
            var rest = new PhysicalPoint(menu.X + (1.25 * r), menu.Y);
            for (var k = 0; k < 60; k++, t += StepUs)
                world.Send(rest, t);

            Check.WithCustomMessage($"DPI {scale}: a 1.25 R creep from the menu spot armed the drop.")
                .That(world.Drops).IsEqualTo(0);

            var dest = new PhysicalPoint(2200, 1600);
            for (var k = 0; k < 30 && world.Drops == 0; k++, t += StepUs)
                world.Send(dest, t);
            Check.That(world.Drops).IsEqualTo(1);
        });
    }

    // ── fixup-01 R2: a move of more than a radius within 3 samples is a new spot ──

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AOneSampleJumpOfMoreThanARadius_NeedsTheFullDwell(double scale)
    {
        // Rest 400 ms, then a 1.1 R jump that stays within 1.5 R of the spot. A re-aim, not
        // drift: the dwell restarts at the landing, so the drop needs the full dwell there —
        // and no more (mutant: the history not cleared on the reset re-fires the rule on the next
        // samples and delays the drop past the dwell plus two samples).
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var t = 0L;
            for (var k = 0; k < 12; k++, t += StepUs)
                world.Send(spot, t);

            var jumpAt = t;
            var landing = new PhysicalPoint(spot.X + (1.1 * r), spot.Y);
            long? drop = null;
            for (var k = 0; k < 40 && drop is null; k++, t += StepUs)
            {
                world.Send(landing, t);
                if (world.Drops > 0) drop = t;
            }

            AssertFullDwellFrom(scale, jumpAt, drop);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AMoveOfMoreThanARadiusOverThreeSamples_NeedsTheFullDwell(double scale)
    {
        // The cursor filter spreads a re-aim over about three samples (owner recording: 82 of the
        // re-aims inside a drop's final 600 ms, none of them a one-sample step). 1.1 R in three
        // 0.367 R steps is still a new spot. Mutants: a one-sample window, a 2 R threshold.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var t = 0L;
            for (var k = 0; k < 12; k++, t += StepUs)
                world.Send(spot, t);

            for (var k = 1; k <= 2; k++, t += StepUs)
                world.Send(new PhysicalPoint(spot.X + (k * 1.1 * r / 3), spot.Y), t);

            var landedAt = t;
            var landing = new PhysicalPoint(spot.X + (1.1 * r), spot.Y);
            long? drop = null;
            for (var k = 0; k < 40 && drop is null; k++, t += StepUs)
            {
                world.Send(landing, t);
                if (world.Drops > 0) drop = t;
            }

            AssertFullDwellFrom(scale, landedAt, drop);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AJumpUnderARadius_KeepsTheProgress(double scale)
    {
        // A 0.9 R jump stays inside the disc as drawn (drawn = hit): the rest goes on.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var t = 0L;
            for (var k = 0; k < 12; k++, t += StepUs)
                world.Send(spot, t);
            long? drop = null;
            for (var k = 0; k < 40 && drop is null; k++, t += StepUs)
            {
                world.Send(new PhysicalPoint(spot.X + (0.9 * r), spot.Y), t);
                if (world.Drops > 0) drop = t;
            }

            Check.WithCustomMessage($"DPI {scale}: a 0.9 R jump lost the progress (drop at {drop}).")
                .That(drop is not null && drop <= (DwellMs * 1000L) + (2 * StepUs)).IsTrue();
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AMoveOfMoreThanARadiusOverFourSamples_KeepsTheProgress(double scale)
    {
        // 1.2 R in four 0.3 R steps: no 3-sample window spans a radius, so it is wander. It
        // crosses the edge once (held by the grace, re-centred) and the rest goes on. Mutant: a
        // 6-sample window calls it a new spot and restarts the dwell.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var t = 0L;
            for (var k = 0; k < 12; k++, t += StepUs)
                world.Send(spot, t);
            for (var k = 1; k <= 3; k++, t += StepUs)
                world.Send(new PhysicalPoint(spot.X + (k * 0.3 * r), spot.Y), t);
            long? drop = null;
            for (var k = 0; k < 40 && drop is null; k++, t += StepUs)
            {
                world.Send(new PhysicalPoint(spot.X + (1.2 * r), spot.Y), t);
                if (world.Drops > 0) drop = t;
            }

            // The crossing costs two sample intervals; everything else counts.
            Check.WithCustomMessage($"DPI {scale}: a 4-sample 1.2 R move lost the progress (drop at {drop}).")
                .That(drop is not null && drop <= (DwellMs * 1000L) + (4 * StepUs)).IsTrue();
        });
    }

    [Theory]
    [InlineData(true, 100)]   // a blink: gap-held samples, shorter than the silence bound
    [InlineData(false, 200)]  // a silence: no samples at all
    public void AResumeAfterALookAway_IsNotComparedWithTheSamplesBeforeIt(bool gapSamples, int awayMs)
    {
        // Rest 300 ms, look away, return 1.2 R off — within the spot. The return is the start of
        // a new run of samples, not a step from the pre-look-away ones: the grace holds the dwell,
        // the target re-centres, and the rest resumes. Mutants: the history not cleared at the gap
        // gate (row 1) or across a silence (row 2) call the return a saccade and restart.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var t = 0L;
            for (var k = 0; k <= 9; k++, t += StepUs)
                world.Send(spot, t);
            var back = t - StepUs + (awayMs * 1000L);
            if (gapSamples)
            {
                for (; t < back; t += StepUs)
                    world.Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(spot, t, IsInGap: true)));
            }
            t = back;

            var returnAt = t;
            long? drop = null;
            for (var k = 0; k < 40 && drop is null; k++, t += StepUs)
            {
                world.Send(new PhysicalPoint(spot.X + (1.2 * r), spot.Y), t);
                if (world.Drops > 0) drop = t;
            }

            Check.WithCustomMessage(
                    $"the return after a {awayMs} ms look-away restarted the dwell (drop {drop! - returnAt} µs after it).")
                .That(drop is not null && drop - returnAt < (DwellMs * 1000L) - (2 * StepUs)).IsTrue();
        });
    }

    private static void AssertFullDwellFrom(double scale, long landedAt, long? drop)
    {
        Check.WithCustomMessage($"DPI {scale}: no drop after the re-aim.").That(drop).IsNotNull();
        var after = drop!.Value - landedAt;
        Check.WithCustomMessage($"DPI {scale}: the re-aim inherited the dwell (drop {after} µs after landing).")
            .That(after).IsGreaterOrEqualThan((DwellMs * 1000L) - StepUs);
        Check.WithCustomMessage($"DPI {scale}: the drop came {after} µs after landing, past the dwell plus two samples.")
            .That(after).IsLessOrEqualThan((DwellMs * 1000L) + (2 * StepUs));
    }

    // ── fixup-01 R3: the drop lands at the centroid of the rest ───────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ARestThatCompletesOnAnOffCentreSample_DropsAtTheRestsCentroid(double scale)
    {
        // The rest wanders ±0.3 R and the sample that completes the dwell is 0.5 R right and
        // 0.4 R down. The owner meant the rest's centre, not that sample. Mutant: drop at the
        // last sample.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var sent = new List<PhysicalPoint>();
            var t = 0L;
            for (var k = 0; k < 40 && world.Drops == 0; k++, t += StepUs)
            {
                var at = k < 19
                    ? new PhysicalPoint(spot.X + (k % 2 == 0 ? -0.3 : 0.3) * r, spot.Y)
                    : new PhysicalPoint(spot.X + (0.5 * r), spot.Y + (0.4 * r));
                sent.Add(at);
                world.Send(at, t);
            }

            Check.That(world.Drops).IsEqualTo(1);
            var centroid = new PhysicalPoint(sent.Average(p => p.X), sent.Average(p => p.Y));
            Check.WithCustomMessage($"DPI {scale}: dropped at {world.DropPoint}, not the rest's centroid {centroid}.")
                .That(world.DropPoint!.Value.DistanceTo(centroid)).IsLessOrEqualThan(1e-6);
            Check.That(world.DropPoint!.Value.DistanceTo(sent[^1])).IsStrictlyGreaterThan(0.3 * r);
        });
    }

    [Fact]
    public void ASaccadeToANewSpot_DropsAtTheNewRestOnly()
    {
        // The centroid restarts with the spot: nothing from the rest before a saccade may pull the
        // drop back towards it. Mutant: the running sum not reset on a new spot.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var p = new PhysicalPoint(900, 700);
            var q = new PhysicalPoint(1500, 1100);
            var t = 0L;
            for (var k = 0; k < 10; k++, t += StepUs)
                world.Send(p, t);
            for (var k = 0; k < 30 && world.Drops == 0; k++, t += StepUs)
                world.Send(q, t);

            Check.That(world.Drops).IsEqualTo(1);
            Check.WithCustomMessage($"dropped at {world.DropPoint}, pulled from Q {q} towards the earlier rest.")
                .That(world.DropPoint!.Value.DistanceTo(q)).IsLessOrEqualThan(1e-6);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AnArmingCreep_DropsAtTheArmedRest_NotTowardsTheMenuSpot(double scale)
    {
        // Fixup-02 T2 (armed only): the latch arms by a slow creep away from the menu spot, with
        // no spot restart on the way (0.2 R a sample; the one crossing re-centres within 1.5 R).
        // The disarmed creep samples are inside the disc but were never credited, so they must
        // not pull the drop back towards the menu. Mutant: disarmed inside samples enter the
        // centroid.
        WithDpi(scale, () =>
        {
            using var world = new World(arm: false);
            var r = world.Trail.ReleaseRadiusPhysical;
            var menu = new PhysicalPoint(1300, 1000);
            var t = 0L;
            for (var k = 0; k <= 8; k++, t += StepUs)
                world.Send(new PhysicalPoint(menu.X + (k * 0.2 * r), menu.Y), t);
            var rest = new PhysicalPoint(menu.X + (1.6 * r), menu.Y);
            for (var k = 0; k < 30 && world.Drops == 0; k++, t += StepUs)
                world.Send(rest, t);

            Check.That(world.Drops).IsEqualTo(1);
            Check.WithCustomMessage($"DPI {scale}: dropped at {world.DropPoint}, pulled from the armed rest {rest} towards the menu.")
                .That(world.DropPoint!.Value.DistanceTo(rest)).IsLessOrEqualThan(1e-6);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ASampleOutsideTheDiscDuringAGracePause_DoesNotMoveTheDropPoint(double scale)
    {
        // Fixup-02 T2 (inside only): the rest walks out in 0.3 R steps and crosses the edge once.
        // That crossing sample is outside the disc as drawn — held by the grace, never credited —
        // so it is not part of the rest the drop lands on. Mutant: every armed sample enters the
        // centroid.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var r = world.Trail.ReleaseRadiusPhysical;
            var spot = new PhysicalPoint(900, 700);
            var sent = new List<PhysicalPoint>();
            var crossing = -1;
            var t = 0L;
            void Send(PhysicalPoint at)
            {
                sent.Add(at);
                world.Send(at, t);
                t += StepUs;
            }

            for (var k = 0; k < 8; k++)
                Send(spot);
            for (var k = 1; k <= 3; k++)
                Send(new PhysicalPoint(spot.X + (k * 0.3 * r), spot.Y));
            crossing = sent.Count;
            var across = new PhysicalPoint(spot.X + (1.2 * r), spot.Y);
            Send(across);
            Check.That(world.Trail.IsReleasePaused).IsTrue();
            for (var k = 0; k < 30 && world.Drops == 0; k++)
                Send(across);

            Check.That(world.Drops).IsEqualTo(1);
            var credited = sent.Where((_, i) => i != crossing).ToList();
            var expected = new PhysicalPoint(credited.Average(p => p.X), credited.Average(p => p.Y));
            var withCrossing = new PhysicalPoint(sent.Average(p => p.X), sent.Average(p => p.Y));
            Check.WithCustomMessage("the scenario must tell the two centroids apart.")
                .That(expected.DistanceTo(withCrossing)).IsStrictlyGreaterThan(1.0);
            Check.WithCustomMessage($"DPI {scale}: the paused crossing sample moved the drop to {world.DropPoint} (expected {expected}).")
                .That(world.DropPoint!.Value.DistanceTo(expected)).IsLessOrEqualThan(1e-6);
        });
    }

    // ── fixup-01 R5: a visit to an exit re-anchors the spot ───────────────────

    [Fact]
    public void AfterAnExitVisit_TheSpotIsBoundedFromWhereTheDwellRestarted()
    {
        // DPI 2, R = 100 px. Rest at P, creep right into an exit zone (r40 at P + 180 px), out of
        // it below, rest 500 ms, then drift to X: 117 px from the zone-entry point but 242 px from
        // P. The dwell restarted at the visit, so X is the same rest and the progress carries.
        // Mutant: the spot left at P makes the move to X a new spot and restarts the dwell.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var p = new PhysicalPoint(1000, 1000);
            world.Trail.SetExitZones(new[] { new DragExitZone(new PhysicalPoint(1180, 1000), 40) });
            var t = 0L;
            for (var k = 0; k < 10; k++, t += StepUs)
                world.Send(p, t);
            foreach (var x in new[] { 1030.0, 1060, 1090, 1120, 1150 })
            {
                world.Send(new PhysicalPoint(x, 1000), t);
                t += StepUs;
            }

            var leave = new PhysicalPoint(1150, 1060);
            for (var k = 0; k < 15; k++, t += StepUs)
                world.Send(leave, t);
            Check.That(world.Drops).IsEqualTo(0);

            long? drop = null;
            var arrivedAt = 0L;
            for (var k = 1; k <= 30 && drop is null; k++, t += StepUs)
            {
                var step = Math.Min(k, 4);
                if (k == 4) arrivedAt = t;
                world.Send(new PhysicalPoint(leave.X + (step * 25), leave.Y + (step * 10)), t);
                if (world.Drops > 0) drop = t;
            }

            Check.WithCustomMessage($"the move after the exit visit restarted the dwell (drop {drop - arrivedAt} µs after arriving).")
                .That(drop is not null && drop - arrivedAt <= 300_000).IsTrue();
        });
    }

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private readonly record struct WanderResult(
        int Samples, int Crossings, int CrossingsBeforeDrop, long DwellUs, PhysicalPoint Spot);

    private sealed class World : IDisposable
    {
        internal static readonly PhysicalPoint Grab = new(100, 100);

        /// <summary>Where the arming sample lands: far from every spot a scenario uses.</summary>
        internal static readonly PhysicalPoint Away = new(3000, 2000);

        /// <param name="arm">
        /// Fixup-01 R1: the release dwell arms only once the gaze has left the spot of the first
        /// sample after the drag starts (the leave-before-arm latch). Every scenario that is not
        /// ABOUT that latch therefore starts with one arming sample at <see cref="Away"/>, one
        /// sample before its own t = 0 — a saccade from the menu to where the scenario rests.
        /// </param>
        internal World(Action<EyeCtrlSettings>? configure = null, bool arm = true)
        {
            Settings = new EyeCtrlSettings();
            Settings.SelectionMode = SelectionMode.ManualSelection;
            Settings.Dwell.FixationRadiusPixels = OwnerFixationRadiusPx;
            configure?.Invoke(Settings);
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
            });
            Messenger.Send(new StartDragTrailMessage(Grab));
            if (arm)
                Send(Away, -StepUs);
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal int Drops { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }

        internal double DetectorRadius =>
            Trail.DetectorForTest.Targets.Single(t => t.Id == "Drag_release").Radius;

        internal void Send(PhysicalPoint at, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(at, t));

        /// <summary>
        /// A saccade to a spot S, three samples of rest, then 3-sample holds cycling through
        /// S + 0.6 R, S + 1.2 R, S + 0.6 R and S along x, while the whole pattern drifts 0.3 DIP
        /// per sample in y. Every move is 0.6 R — under a radius per 3-sample window, so it is
        /// wander, not a saccade (fixup-01 R2). The hold at 1.2 R lands outside the circle centred
        /// on S, and the hold back at S lands outside the circle re-centred at 1.2 R: two crossings
        /// per cycle, all within 1.5 R of S.
        /// </summary>
        internal WanderResult DriveCrossingWander(double r, int maxSamples)
        {
            var spot = new PhysicalPoint(900, 700);
            var drift = DpiHelper.LogicalToPhysicalX(0.3);
            var crossings = 0;
            var crossingsBeforeDrop = 0;
            long dwellStart = 0, dwellUs = 0;
            var samples = 0;

            for (var i = 0; i < maxSamples; i++, samples++)
            {
                var t = i * StepUs;
                PhysicalPoint at;
                if (i < 3)
                    at = spot;
                else
                {
                    var hold = (i - 3) / 3;
                    var x = (hold % 4) switch { 0 => 0.6, 1 => 1.2, 2 => 0.6, _ => 0.0 } * r;
                    at = new PhysicalPoint(spot.X + x, spot.Y + (i * drift));
                }

                var wasCentre = Trail.ReleaseButtonCenter;
                Send(at, t);
                // The saccade's landing sample is inside the target R2 placed on it: the dwell starts there.
                if (i == 0) dwellStart = t;
                if (i >= 3 && wasCentre != Trail.ReleaseButtonCenter)
                {
                    crossings++;
                    if (Drops == 0) crossingsBeforeDrop++;
                }
                if (Drops > 0 && dwellUs == 0)
                    dwellUs = t - dwellStart;
            }

            return new WanderResult(samples, crossings, crossingsBeforeDrop, dwellUs, spot);
        }

        public void Dispose() => Trail.Dispose();
    }
}
