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
            using var world = new World();
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
        // spot the dwell restarts (PX2/R4). Mutant: measuring 1.5 radii from the current centre
        // instead lets the progress ride the whole walk and drops mid-walk.
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
            Check.That(world.DropPoint).IsEqualTo(last);
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
            world.Send(spot, 0);
            for (var i = 1; i <= 10; i++)
                world.Send(spot, i * StepUs);

            // 9 credited intervals of 18.
            Check.That(world.Trail.ReleaseProgress).IsCloseTo(9 * StepUs / (DwellMs * 1000.0), 1e-6);
            Check.That(world.Trail.IsReleasePaused).IsFalse();
            var held = world.Trail.ReleaseProgress;

            // A crossing: outside the drawn circle, inside the spot — held, not reset.
            var across = new PhysicalPoint(spot.X + (1.2 * r), spot.Y);
            world.Send(across, 11 * StepUs);
            Check.That(world.Trail.IsReleasePaused).IsTrue();
            Check.That(world.Trail.ReleaseProgress).IsCloseTo(held, 1e-9);
            Check.That(world.Trail.ReleaseDisc.Paused).IsTrue();

            world.Send(across, 12 * StepUs);
            Check.That(world.Trail.IsReleasePaused).IsFalse();
            world.Send(across, 13 * StepUs);
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

        internal World(Action<EyeCtrlSettings>? configure = null)
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
        /// A saccade to a spot S, two samples of rest, then holds of six samples alternating
        /// between S + 1.2 R and S − 0.25 R along x, while the whole pattern drifts 0.3 DIP per
        /// sample in y. Every hold lands outside the circle the previous one left (1.2 R, then
        /// 1.45 R away) but within 1.5 R of S, so every hold starts with a crossing.
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
                    var hold = (i - 3) / 6;
                    var x = hold % 2 == 0 ? 1.2 * r : -0.25 * r;
                    at = new PhysicalPoint(spot.X + x, spot.Y + (i * drift));
                }

                var wasCentre = Trail.ReleaseButtonCenter;
                Send(at, t);
                if (i == 1) dwellStart = t; // the first sample inside the placed circle
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
