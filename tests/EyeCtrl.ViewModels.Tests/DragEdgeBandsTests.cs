namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C6: the drag's drop dwell gets C4's two off-screen bands. A rest near an edge used to lose the
/// whole drop to one filtered sample 11–30 px past the edge (the sample counted as an exit, which
/// reset the dwell and restarted the spot). Now:
/// <list type="bullet">
/// <item>inside the published tolerance (10 px) a sample is ordinary and credited;</item>
/// <item>between the tolerance and C4's 30 px disarm margin it is WITHHELD — no credit, no reset,
/// no re-centre, no centroid entry; the release dwell's normal pause grace holds it;</item>
/// <item>past the margin it is A5 H1's summon gesture, exactly today's clean break.</item>
/// </list>
/// <para>
/// Every row runs the real <see cref="DragTrailViewModel"/> in Manual Selection (the release dwell
/// is the only drop path being measured), at 30 Hz, with the owner's fixation radius of 80 px and
/// the shipped 500 ms pause grace, at DPI 1 and 2. The baseline row of each pair is the same rest
/// with no excursion, so a row measures the excursion's own cost, not the harness.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragEdgeBandsTests
{
    private const long StepUs = 33_333;            // 30 Hz
    private const long DwellUs = 600_000;          // DragReleaseButtonDwellMs default
    private const int ExcursionSample = 15;        // ~500 ms into the dwell
    private const double OwnerFixationRadiusPx = 80;
    private const double RestInsetPx = 17;         // the owner's scrollbar: ~17 px inside the edge

    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint Grab = new(100, 100);

    /// <summary>Far from every rest below, and the sample that arms C2's leave-before-arm latch.</summary>
    private static readonly PhysicalPoint Away = new(3000, 2000);

    private static PhysicalPoint EdgeRest() =>
        new(Screen.X + Screen.Width - RestInsetPx, Screen.Y + (Screen.Height / 2));

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the tolerance boundary ────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 5)]
    [InlineData(1.0, 10)]
    [InlineData(2.0, 5)]
    [InlineData(2.0, 10)]
    public void ASampleInsideTheTolerance_IsAnOrdinarySample(double scale, int pastEdgePx)
    {
        // C6 keeps A5 H1's 10 px tolerance exactly: a border sample inside it is a normal,
        // credited sample, so the edge row stays usable.
        // Mutation: treat any past-edge sample as withheld → RED (the excursion costs a frame).
        WithDpi(scale, () =>
        {
            var rest = EdgeRest();
            using var baseline = new World();
            var without = HoldUntilDrop(baseline, rest, edgeOffsetPx: null);

            using var edged = new World();
            var with = HoldUntilDrop(edged, rest, edgeOffsetPx: pastEdgePx);

            Check.WithCustomMessage($"DPI {scale}: the baseline rest never dropped.")
                 .That(without).IsNotNull();
            Check.WithCustomMessage(
                    $"DPI {scale}: a rest near the edge with a sample {pastEdgePx} px past it "
                    + "(inside the 10 px tolerance) did not drop.")
                 .That(with).IsNotNull();
            Check.WithCustomMessage(
                    $"DPI {scale}: a {pastEdgePx} px sample inside the tolerance cost "
                    + $"{with!.Value - without!.Value} µs of the dwell.")
                 .That(with.Value - without.Value).IsLessOrEqualThan(StepUs);
        });
    }

    // ── the two bands ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 25, false)]
    [InlineData(2.0, 25, false)]
    [InlineData(1.0, 35, true)]
    [InlineData(2.0, 35, true)]
    public void PastTheTolerance_25PxIsWithheld_And35PxIsACleanBreak(
        double scale, int pastEdgePx, bool restarts)
    {
        // The ticket's pins, in C4's pattern. A sample 25 px past the edge may not restart the drop
        // dwell: it is withheld, so the rest drops on the dwell + grace bookkeeping. A sample 35 px
        // past is past C4's 30 px disarm margin and is exactly the old clean break: the return
        // needs a full dwell from the first on-screen sample.
        // Mutants: the withheld band credited (the 25 px row loses its at-least-a-frame cost), the
        // withheld band still resetting (the 25 px row approaches a full restart), or the margin
        // decoupled from C4's constant / moved to 10 (the 35 px row no longer restarts).
        WithDpi(scale, () =>
        {
            var rest = EdgeRest();
            using var baseline = new World();
            var without = HoldUntilDrop(baseline, rest, edgeOffsetPx: null);

            using var edged = new World();
            var with = HoldUntilDrop(edged, rest, edgeOffsetPx: pastEdgePx);

            Check.WithCustomMessage($"DPI {scale}: the baseline rest never dropped.")
                 .That(without).IsNotNull();
            Check.WithCustomMessage(
                    $"DPI {scale}: a rest near the edge with one sample {pastEdgePx} px past it "
                    + "did not drop at all.")
                 .That(with).IsNotNull();

            if (!restarts)
            {
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample was credited — the excursion cost "
                        + $"only {with!.Value - without!.Value} µs.")
                     .That(with.Value - without.Value).IsGreaterOrEqualThan(StepUs);
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample restarted the dwell — the drop "
                        + $"moved {with.Value - without.Value} µs past the baseline.")
                     .That(with.Value - without.Value).IsLessOrEqualThan(3 * StepUs);
            }
            else
            {
                var backAt = (ExcursionSample + 1) * StepUs;
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample did not restart the dwell — the "
                        + $"drop came {with!.Value - backAt} µs after the return, not a full dwell.")
                     .That(with.Value - backAt).IsGreaterOrEqualThan(DwellUs);
                Check.WithCustomMessage(
                        $"DPI {scale}: the restart did not complete on the first sample past the dwell "
                        + $"({with.Value - backAt} µs after the return).")
                     .That(with.Value - backAt).IsLessOrEqualThan(DwellUs + StepUs);
            }
        });
    }

    // ── what "withheld" means ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheWithheldSample_IsPaused_DoesNotMoveTheDisc_AndNeverEntersTheCentroid(double scale)
    {
        // Withheld means withheld. The sample is outside the release dwell, so the pause grace holds
        // the progress; the disc stays on the rest it belongs to; and the drop lands on that rest's
        // centroid. The excursion here is more than one radius away, so a withheld sample that fell
        // through to the normal path would re-centre the disc (and a saccade restart would too); a
        // sample added to the rest centroid would visibly shift the drop.
        // Mutants: the withheld sample credited, still re-centring, or entering the centroid → RED.
        WithDpi(scale, () =>
        {
            using var world = new World();
            var rest = new PhysicalPoint(3000, 1200);
            var edge = new PhysicalPoint(Screen.X + Screen.Width + 25, rest.Y);

            for (var i = 0; i < 12; i++)
                world.Send(rest, i * StepUs);   // 366 ms at the rest
            var progress = world.Trail.ReleaseProgress;
            Check.That(progress).IsStrictlyGreaterThan(0);

            world.Send(edge, 12 * StepUs);

            Check.WithCustomMessage("the withheld sample was credited.")
                 .That(world.Trail.ReleaseProgress).IsCloseTo(progress, 1e-12);
            Check.WithCustomMessage("the withheld sample did not pause the release dwell.")
                 .That(world.Trail.IsReleasePaused).IsTrue();
            Check.WithCustomMessage("the withheld sample moved the disc.")
                 .That(world.Trail.ReleaseButtonCenter).IsEqualTo(rest);
            Check.That(world.Trail.ReleaseDisc.Center).IsEqualTo(rest);

            long? dropAt = null;
            for (var i = 13; i < 40 && dropAt is null; i++)
            {
                world.Send(rest, i * StepUs);
                if (world.Drops > 0) dropAt = i * StepUs;
            }

            Check.WithCustomMessage("the rest never dropped after the withheld sample.")
                 .That(dropAt).IsNotNull();
            Check.WithCustomMessage($"DPI {scale}: the drop did not land on the rest's centroid.")
                 .That(world.DropPoint!.Value.DistanceTo(rest)).IsLessOrEqualThan(1e-9);
        });
    }

    /// <summary>
    /// Holds a rest at 30 Hz for up to two seconds; when <paramref name="edgeOffsetPx"/> is set,
    /// one sample in the middle of the dwell (at <see cref="ExcursionSample"/>) is replaced by a
    /// point that many px past the right screen edge. Returns the drop's timestamp, or null.
    /// </summary>
    private static long? HoldUntilDrop(World world, PhysicalPoint rest, int? edgeOffsetPx)
    {
        for (var i = 0; i < 60; i++)
        {
            var t = i * StepUs;
            var at = i == ExcursionSample && edgeOffsetPx is { } off
                ? new PhysicalPoint(Screen.X + Screen.Width + off, rest.Y)
                : rest;
            world.Send(at, t);
            if (world.Drops > 0) return t;
        }
        return null;
    }

    private sealed class World : IDisposable
    {
        internal World()
        {
            Settings = new EyeCtrlSettings { SelectionMode = SelectionMode.ManualSelection };
            Settings.Dwell.FixationRadiusPixels = OwnerFixationRadiusPx;
            Settings.Dwell.DwellPauseGraceMs = 500;
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Trail.SetScreenBounds(Screen, 10);
            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
            });
            Messenger.Send(new StartDragTrailMessage(Grab));

            // C2 fixup-01 R1: the release arms only once the gaze has left the spot of the first
            // sample after the drag starts. This sample is that leave, one frame before the rest.
            Send(Away, -StepUs);
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal int Drops { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }

        internal void Send(PhysicalPoint at, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(at, t));

        public void Dispose() => Trail.Dispose();
    }
}
