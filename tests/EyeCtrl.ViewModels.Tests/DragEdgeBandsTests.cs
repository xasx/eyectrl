namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C6/C7: the drag's drop dwell gets C4's two off-screen bands, edge-aware. A rest near an edge
/// used to lose the whole drop to one filtered sample 11–30 px past the edge (the sample counted as
/// an exit, which reset the dwell and restarted the spot). Now:
/// <list type="bullet">
/// <item>inside the published tolerance (10 px) a sample is ordinary and credited;</item>
/// <item>between the tolerance and C4's 30 px disarm margin it is WITHHELD on an edge that carries
/// a summon gesture (C6) — no credit, no reset, no re-centre, no centroid entry; the release
/// dwell's normal pause grace holds it — and CREDITED on an edge with no summon gesture (C7), with
/// the drop point clamped back on-screen;</item>
/// <item>past the margin it is A5 H1's summon gesture, exactly today's clean break, on every
/// edge.</item>
/// </list>
/// <para>
/// Every row runs the real <see cref="DragTrailViewModel"/> in Manual Selection (the release dwell
/// is the only drop path being measured), at 30 Hz, with the owner's fixation radius of 80 px and
/// the shipped 500 ms pause grace, at DPI 1 and 2. The baseline row of each pair is the same rest
/// with no excursion, so a row measures the excursion's own cost, not the harness. The owner's
/// summon set is Top (toolbar docked, auto-hide) and Left (Quick Actions).
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

    /// <summary>The owner's summon edges: the toolbar docks Top, Quick Actions summons Left.</summary>
    private const DragSummonEdges OwnerSummons = DragSummonEdges.Top | DragSummonEdges.Left;

    /// <summary>The four screen edges, for the per-edge-class rows.</summary>
    public enum ScreenEdge { Top, Bottom, Left, Right }

    /// <summary>Far from every rest below, and the sample that arms C2's leave-before-arm latch.</summary>
    private static readonly PhysicalPoint Away = new(3000, 2000);

    private static PhysicalPoint EdgeRest() =>
        new(Screen.X + Screen.Width - RestInsetPx, Screen.Y + (Screen.Height / 2));

    private static PhysicalPoint RestAt(ScreenEdge edge, double insetPx) => edge switch
    {
        ScreenEdge.Top => new(Screen.CenterX, Screen.Y + insetPx),
        ScreenEdge.Bottom => new(Screen.CenterX, Screen.Y + Screen.Height - insetPx),
        ScreenEdge.Left => new(Screen.X + insetPx, Screen.CenterY),
        _ => new(Screen.X + Screen.Width - insetPx, Screen.CenterY),
    };

    private static PhysicalPoint PastEdge(ScreenEdge edge, double depthPx) => edge switch
    {
        ScreenEdge.Top => new(Screen.CenterX, Screen.Y - depthPx),
        ScreenEdge.Bottom => new(Screen.CenterX, Screen.Y + Screen.Height + depthPx),
        ScreenEdge.Left => new(Screen.X - depthPx, Screen.CenterY),
        _ => new(Screen.X + Screen.Width + depthPx, Screen.CenterY),
    };

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
        // credited sample, so the edge row stays usable. C7 does not touch the tolerance on any
        // edge — the owner's right edge is a non-summon edge here, and the sample is still inside
        // the tolerance, so the summon set cannot matter.
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

    // ── the two bands, per edge class (C6 + C7) ──────────────────────────────

    [Theory]
    [InlineData(1.0, 25, DragSummonEdges.Right, true)]
    [InlineData(2.0, 25, DragSummonEdges.Right, true)]
    [InlineData(1.0, 25, DragSummonEdges.None, false)]
    [InlineData(2.0, 25, DragSummonEdges.None, false)]
    [InlineData(1.0, 35, DragSummonEdges.Right, false)]
    [InlineData(2.0, 35, DragSummonEdges.Right, false)]
    [InlineData(1.0, 35, DragSummonEdges.None, false)]
    [InlineData(2.0, 35, DragSummonEdges.None, false)]
    public void PastTheTolerance_TheSummonEdgeWithholds_ANonSummonEdgeCredits_AndBothBreakPastTheMargin(
        double scale, int pastEdgePx, DragSummonEdges summonEdges, bool withheld)
    {
        // C6's pins, in C4's pattern, now per edge class. A sample 25 px past a SUMMON edge may not
        // restart the drop dwell: it is withheld, so the rest drops on the dwell + grace
        // bookkeeping. The same sample past an edge with NO summon gesture is credited (C7), so it
        // costs the drop nothing. A sample 35 px past is past C4's 30 px disarm margin on every
        // edge and is exactly the old clean break: the return needs a full dwell from the first
        // on-screen sample.
        // Mutants: the summon edge credited (the 25 px summon row loses its at-least-a-frame cost),
        // any edge withheld (the 25 px non-summon row gains one), the withheld band still resetting,
        // or the margin decoupled from C4's constant / moved to 10 (the 35 px rows stop restarting).
        WithDpi(scale, () =>
        {
            var restarts = pastEdgePx > InteractionManager.RepeatDisarmMarginPhysical;
            var rest = EdgeRest();
            using var baseline = new World(summonEdges);
            var without = HoldUntilDrop(baseline, rest, edgeOffsetPx: null);

            using var edged = new World(summonEdges);
            var with = HoldUntilDrop(edged, rest, edgeOffsetPx: pastEdgePx);

            Check.WithCustomMessage($"DPI {scale}: the baseline rest never dropped.")
                 .That(without).IsNotNull();
            Check.WithCustomMessage(
                    $"DPI {scale}: a rest near the edge with one sample {pastEdgePx} px past it "
                    + "did not drop at all.")
                 .That(with).IsNotNull();

            var cost = with!.Value - without!.Value;
            if (restarts)
            {
                var backAt = (ExcursionSample + 1) * StepUs;
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample did not restart the dwell — the "
                        + $"drop came {with.Value - backAt} µs after the return, not a full dwell.")
                     .That(with.Value - backAt).IsGreaterOrEqualThan(DwellUs);
                Check.WithCustomMessage(
                        $"DPI {scale}: the restart did not complete on the first sample past the dwell "
                        + $"({with.Value - backAt} µs after the return).")
                     .That(with.Value - backAt).IsLessOrEqualThan(DwellUs + StepUs);
            }
            else if (withheld)
            {
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample on a summon edge was credited — "
                        + $"the excursion cost only {cost} µs.")
                     .That(cost).IsGreaterOrEqualThan(StepUs);
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample on a summon edge restarted the "
                        + $"dwell — the drop moved {cost} µs past the baseline.")
                     .That(cost).IsLessOrEqualThan(3 * StepUs);
            }
            else
            {
                Check.WithCustomMessage(
                        $"DPI {scale}: the {pastEdgePx} px sample on an edge with no summon gesture "
                        + $"cost the drop {cost} µs, so it was not credited.")
                     .That(cost).IsEqualTo(0L);
            }
        });
    }

    // The disarm margin is C4's constant; a test copy would let the two drift apart (C6's own
    // mutation). Read through the public constant so the 35 px rows always mean "past the margin".

    /// <summary>
    /// C7 per edge: the 10–30 px band is withheld on a summon edge (no credit, paused, disc stays)
    /// and credited on an edge with no summon gesture (progress advances, not paused). The four
    /// edges are exercised, because "which edge" is the whole rule.
    /// Mutation: any edge treated as summon (or any as non-summon) → RED.
    /// </summary>
    [Theory]
    [InlineData(ScreenEdge.Top, DragSummonEdges.Top, true)]
    [InlineData(ScreenEdge.Top, DragSummonEdges.None, false)]
    [InlineData(ScreenEdge.Bottom, DragSummonEdges.Bottom, true)]
    [InlineData(ScreenEdge.Bottom, DragSummonEdges.None, false)]
    [InlineData(ScreenEdge.Left, DragSummonEdges.Left, true)]
    [InlineData(ScreenEdge.Left, DragSummonEdges.None, false)]
    [InlineData(ScreenEdge.Right, DragSummonEdges.Right, true)]
    [InlineData(ScreenEdge.Right, DragSummonEdges.None, false)]
    public void TheBandIsWithheldOnlyOnASummonEdge(
        ScreenEdge edge, DragSummonEdges summonEdges, bool withheld)
    {
        WithDpi(1.0, () =>
        {
            using var world = new World(summonEdges);
            var rest = RestAt(edge, RestInsetPx);
            for (var i = 0; i < 12; i++)
                world.Send(rest, i * StepUs);
            var progress = world.Trail.ReleaseProgress;
            Check.That(progress).IsStrictlyGreaterThan(0);

            world.Send(PastEdge(edge, 25), 12 * StepUs);

            if (withheld)
            {
                Check.WithCustomMessage($"{edge}: the band sample was credited.")
                     .That(world.Trail.ReleaseProgress).IsCloseTo(progress, 1e-12);
                Check.WithCustomMessage($"{edge}: the band sample did not pause the release dwell.")
                     .That(world.Trail.IsReleasePaused).IsTrue();
                Check.WithCustomMessage($"{edge}: the band sample moved the disc.")
                     .That(world.Trail.ReleaseButtonCenter).IsEqualTo(rest);
            }
            else
            {
                Check.WithCustomMessage($"{edge}: the band sample on a non-summon edge was withheld.")
                     .That(world.Trail.ReleaseProgress).IsStrictlyGreaterThan(progress);
                Check.WithCustomMessage($"{edge}: the credited band sample left the dwell paused.")
                     .That(world.Trail.IsReleasePaused).IsFalse();
            }
        });
    }

    /// <summary>
    /// C7: in a corner the stricter edge wins — one summon edge in the pair withholds the sample.
    /// Mutation: only the nearest/first edge consulted → the top-summon row credits (RED); no edge
    /// consulted → both summon rows credit (RED).
    /// </summary>
    [Theory]
    [InlineData(DragSummonEdges.Top, true)]
    [InlineData(DragSummonEdges.Right, true)]
    [InlineData(DragSummonEdges.None, false)]
    public void ACornerBandSample_WithOneSummonEdge_IsWithheld(
        DragSummonEdges summonEdges, bool withheld)
    {
        WithDpi(1.0, () =>
        {
            using var world = new World(summonEdges);
            var rest = new PhysicalPoint(Screen.X + Screen.Width - RestInsetPx, Screen.Y + RestInsetPx);
            for (var i = 0; i < 12; i++)
                world.Send(rest, i * StepUs);
            var progress = world.Trail.ReleaseProgress;

            var corner = new PhysicalPoint(
                Screen.X + Screen.Width + 25, Screen.Y - 25);   // past BOTH edges, inside 30 px
            world.Send(corner, 12 * StepUs);

            if (withheld)
            {
                Check.WithCustomMessage("the corner sample over a summon edge was credited.")
                     .That(world.Trail.ReleaseProgress).IsCloseTo(progress, 1e-12);
                Check.WithCustomMessage("the corner sample over a summon edge moved the disc.")
                     .That(world.Trail.ReleaseButtonCenter).IsEqualTo(rest);
            }
            else
            {
                Check.WithCustomMessage("the corner sample with no summon edge was withheld.")
                     .That(world.Trail.ReleaseProgress).IsStrictlyGreaterThan(progress);
            }
        });
    }

    /// <summary>
    /// C7: crediting the band must not drop into an off-screen point. A rest that sits in the
    /// non-summon right edge's band accrues its credit 27 px past the screen, so its centroid is
    /// ~25 px outside; the drop must land clamped on the border, not at the off-screen centroid.
    /// Mutation: no clamp → the drop point is ~3865 (RED). A point still inside the 10 px
    /// tolerance keeps C6/A5's behaviour (pinned by the App's
    /// <c>DragToolbarPauseExitZoneTests.ARestJustPastTheEdge_InsideTheTolerance_StillDrops</c>).
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ACreditedBandSample_DropsClampedOnScreen(double scale)
    {
        WithDpi(scale, () =>
        {
            using var world = new World(OwnerSummons);
            const double y = 1200;
            var anchor = new PhysicalPoint(Screen.X + Screen.Width - 5, y);   // inside: places the disc
            var band = new PhysicalPoint(Screen.X + Screen.Width + 27, y);    // 27 px past, inside 30

            long? dropAt = null;
            world.Send(anchor, 0);
            for (var i = 1; i < 40 && dropAt is null; i++)
            {
                world.Send(band, i * StepUs);
                if (world.Drops > 0) dropAt = i * StepUs;
            }

            Check.WithCustomMessage($"DPI {scale}: the band rest did not drop.")
                 .That(dropAt).IsNotNull();
            Check.WithCustomMessage(
                    $"DPI {scale}: the drop landed off-screen at "
                    + $"{world.DropPoint!.Value.X:0.##} (screen width {Screen.Width:0}).")
                 .That(world.DropPoint.Value.X).IsEqualTo(Screen.X + Screen.Width);
            Check.WithCustomMessage($"DPI {scale}: the drop's Y was clamped too.")
                 .That(world.DropPoint.Value.Y).IsCloseTo(y, 1e-9);

            // Every sample is credited, so the dwell keeps its normal schedule: the drop is not
            // spread over twice the dwell as it would be with the band withheld.
            Check.WithCustomMessage(
                    $"DPI {scale}: the band was not credited — the drop took {dropAt} µs.")
                 .That(dropAt!.Value).IsLessOrEqualThan(DwellUs + StepUs);
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
        // C7: the right edge is a summon edge here, so the excursion keeps C6's withheld band.
        WithDpi(scale, () =>
        {
            using var world = new World(DragSummonEdges.Right);
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
            // M8: a withheld sample must not enter the saccade history. If it did, the return to
            // the rest (865 px from the excursion) would look like a saccade and restart the
            // dwell, pushing the drop out by a full 600 ms.
            Check.WithCustomMessage(
                    $"DPI {scale}: the withheld sample's far excursion restarted the dwell "
                    + $"(drop at {dropAt!.Value} µs).")
                 .That(dropAt.Value).IsStrictlyLessThan(800_000);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AStraddlingHover_CreditsOnlyTheOnScreenSamples_AndNeverResets(double scale)
    {
        // C6 fixup-01 R2: a real summon hover straddles the 10 px line — samples at −5 px are
        // on-screen (credited), samples at −15 px are withheld. With variant A only the −5 px
        // samples credit the release dwell: the hover accumulates ~15 of the 18 credited intervals
        // it would need, never resets, and never drops in 1.5 s. Variant B (the −15 px samples
        // credited) reaches the dwell at about 600 ms and drops — that is the mutant. C7 keeps the
        // top edge withheld because the owner's docked toolbar summons there.
        WithDpi(scale, () =>
        {
            using var world = new World(DragSummonEdges.Top);
            var onScreen = new PhysicalPoint(1920, -5);    // 5 px above the top edge: inside the 10 px tolerance
            var withheld = new PhysicalPoint(1920, -15);   // 15 px above: withheld

            var progress = new List<double>();
            for (var i = 0; i < 45 && world.Drops == 0; i++)   // 1.5 s at 30 Hz
            {
                world.Send(i % 3 == 2 ? withheld : onScreen, i * StepUs);
                if (i >= 2) progress.Add(world.Trail.ReleaseProgress);
            }

            Check.WithCustomMessage($"DPI {scale}: the straddling hover dropped the item.")
                 .That(world.Drops).IsEqualTo(0);
            Check.WithCustomMessage(
                    $"DPI {scale}: the hover did not credit only its on-screen samples "
                    + $"({world.Trail.ReleaseProgress:0.#####}).")
                 .That(world.Trail.ReleaseProgress)
                 .IsCloseTo(15.0 * StepUs / DwellUs, 1e-9);
            Check.WithCustomMessage($"DPI {scale}: a withheld sample reset the hover.")
                 .That(progress.Count(p => p <= 0.0)).IsEqualTo(0);
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
        internal World(DragSummonEdges summonEdges = OwnerSummons)
        {
            Settings = new EyeCtrlSettings { SelectionMode = SelectionMode.ManualSelection };
            Settings.Dwell.FixationRadiusPixels = OwnerFixationRadiusPx;
            Settings.Dwell.DwellPauseGraceMs = 500;
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Trail.SetScreenBounds(Screen, 10, summonEdges);
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
