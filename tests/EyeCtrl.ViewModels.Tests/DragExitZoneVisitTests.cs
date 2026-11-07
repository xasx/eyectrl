namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S8 B11 (sitting 07.11): the drop disc on an exit visit. The disc used to follow the gaze INTO the
/// ✕'s zone and was drawn over it — the "overlap" the owner saw. Now, while a sample's band is Exit:
/// <list type="bullet">
/// <item>the disc does not follow (and a saccade into the zone does not move it either);</item>
/// <item>the disc is hidden;</item>
/// <item>the first on-screen sample after the visit re-centres it onto ITSELF and shows it, and the
/// drop there needs a full fresh dwell.</item>
/// </list>
/// <para>
/// The owner's geometry at DPI 2.0: the ✕ at (3660, 1080) on a 3840×2400 screen, its no-drop zone
/// 2 × 100 px (the factor itself is the coordinator's and is pinned by the App's
/// <c>DragCoordinatorExitZoneWiringTests</c>; here the zone is supplied, as the coordinator does).
/// Manual Selection, so the release dwell is the only drop path being measured.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragExitZoneVisitTests
{
    private const long StepUs = 33_333;            // 30 Hz
    private const long DwellUs = 600_000;          // DragReleaseButtonDwellMs default
    private const double ZoneRadiusPx = 200;       // 2 × the r50 button at DPI 2.0

    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint Cross = new(3660, 1080);

    /// <summary>A rest 300 px left of the ✕: outside its 200 px zone.</summary>
    private static readonly PhysicalPoint Rest = new(Cross.X - 300, Cross.Y);

    /// <summary>Inside the zone, 150 px left of the ✕ (and 150 px from the rest: a saccade).</summary>
    private static readonly PhysicalPoint InZone = new(Cross.X - 150, Cross.Y);

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void InsideTheZone_TheDiscIsHidden_AndNeverMovesOntoTheCross()
    {
        // Mutants: FollowGaze still run for Exit samples (M3) → the centre moves to the zone sample;
        // Hidden always false (M4) → the disc is drawn over the ✕.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            for (var i = 0; i < 12; i++)
                world.Send(Rest, i * StepUs);
            Check.That(world.Trail.ReleaseButtonCenter).IsEqualTo(Rest);
            Check.That(world.Trail.IsReleaseHidden).IsFalse();

            world.Send(InZone, 12 * StepUs);
            Check.WithCustomMessage("the drop disc is still drawn inside the ✕'s no-drop zone.")
                 .That(world.Trail.IsReleaseHidden).IsTrue();
            Check.That(world.Trail.ReleaseDisc.Hidden).IsTrue();
            Check.WithCustomMessage("the disc followed the gaze into the zone.")
                 .That(world.Trail.ReleaseButtonCenter).IsEqualTo(Rest);

            // A long rest right on the ✕: still hidden, still not there, and never a drop.
            for (var i = 13; i < 60; i++)
                world.Send(Cross, i * StepUs);
            Check.That(world.Trail.IsReleaseHidden).IsTrue();
            Check.WithCustomMessage("the disc slid onto the ✕.")
                 .That(world.Trail.ReleaseButtonCenter).IsEqualTo(Rest);
            Check.That(world.Trail.ReleaseDisc.Center).IsEqualTo(Rest);
            Check.That(world.Drops).IsEqualTo(0);
        });
    }

    [Fact]
    public void TheFirstSampleAfterTheVisit_ReCentresTheDisc_AndNeedsAFullFreshDwell()
    {
        // The return sample sits INSIDE the disc as it was left before the visit (90 px from the
        // rest, radius 100 px) and within one radius of the zone samples (no saccade), so only the
        // explicit re-centre can move the disc there. Mutant M5 (no re-centre on the return) → the
        // centre stays on the old rest.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            for (var i = 0; i < 15; i++)
                world.Send(Rest, i * StepUs);                       // ~470 ms at the rest
            Check.That(world.Trail.ReleaseProgress).IsStrictlyGreaterThan(0.5);

            var zoneEdge = new PhysicalPoint(Cross.X - 190, Cross.Y);  // 10 px inside the zone
            for (var i = 15; i < 18; i++)
                world.Send(zoneEdge, i * StepUs);                   // ~100 ms in the zone
            Check.That(world.Trail.IsReleaseHidden).IsTrue();

            var back = new PhysicalPoint(Cross.X - 210, Cross.Y);  // 10 px outside, 90 px from Rest
            var backAt = 18 * StepUs;
            world.Send(back, backAt);

            Check.WithCustomMessage("the disc was not shown again on the first sample after the visit.")
                 .That(world.Trail.IsReleaseHidden).IsFalse();
            Check.WithCustomMessage("the disc did not re-centre onto the first sample after the visit.")
                 .That(world.Trail.ReleaseButtonCenter).IsEqualTo(back);
            Check.WithCustomMessage("progress from before the visit carried over.")
                 .That(world.Trail.ReleaseProgress).IsLessOrEqualThan((double)StepUs / DwellUs);

            long? dropAt = null;
            for (var i = 19; i < 60 && dropAt is null; i++)
            {
                world.Send(back, i * StepUs);
                if (world.Drops > 0) dropAt = i * StepUs;
            }

            Check.That(dropAt).IsNotNull();
            Check.WithCustomMessage($"the drop came {dropAt!.Value - backAt} µs after the return, not a full dwell.")
                 .That(dropAt.Value - backAt).IsGreaterOrEqualThan(DwellUs);
            Check.That(dropAt.Value - backAt).IsLessOrEqualThan(DwellUs + StepUs);
            Check.That(world.DropPoint!.Value.DistanceTo(back)).IsLessOrEqualThan(1e-9);
        });
    }

    [Fact]
    public void ARestInsideTheZone_NeverDrops_EvenAtItsEdge()
    {
        // The zone is the whole no-drop area, not only the drawn ✕: a rest 150 px from the ✕
        // centre — the owner's near miss was 145 px — never drops.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            for (var i = 0; i < 90; i++)
                world.Send(InZone, i * StepUs);                     // 3 s

            Check.That(world.Drops).IsEqualTo(0);
            Check.That(world.Trail.IsReleaseHidden).IsTrue();
        });
    }

    [Fact]
    public void TheDropCarriesItsDiagnostics()
    {
        // The drag-end log line's fields, filled by the view model at the drop: one exit entry, the
        // rest's credited samples, the disc as drawn, and the raw (unclamped) centroid.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            world.Send(InZone, 0);
            var back = new PhysicalPoint(Cross.X - 260, Cross.Y);
            for (var i = 1; i < 40 && world.Drops == 0; i++)
                world.Send(back, i * StepUs);

            var d = world.Diagnostics;
            Check.That(d).IsNotNull();
            Check.That(d!.Trigger).IsEqualTo(DragDropTrigger.ReleaseDwell);
            Check.That(d.ExitZoneEntries).IsEqualTo(1);
            Check.That(d.RawPoint).IsEqualTo(back);
            Check.That(d.DiscCenter).IsEqualTo(back);
            Check.That(d.DiscRadiusPhysical).IsEqualTo(100.0);
            Check.That(d.RestCount).IsStrictlyGreaterThan(17);
            Check.That(d.RestWithheldCount).IsEqualTo(0);
        });
    }

    [Fact]
    public void TheDropDiagnostics_CountTheRestsWithheldSamples()
    {
        // C6: a rest by the top (summon) edge with one sample 20 px past it — withheld.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var rest = new PhysicalPoint(1920, 17);
            var past = new PhysicalPoint(1920, -20);
            for (var i = 0; i < 40 && world.Drops == 0; i++)
                world.Send(i == 8 ? past : rest, i * StepUs);

            Check.That(world.Drops).IsEqualTo(1);
            Check.That(world.Diagnostics!.RestWithheldCount).IsEqualTo(1);
            Check.That(world.Diagnostics.ExitZoneEntries).IsEqualTo(0);
        });
    }

    [Fact]
    public void ASettleDrop_IsTaggedAsASettle()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World(SelectionMode.AutoSelection);
            for (var i = 0; i < 40 && world.Drops == 0; i++)
                world.Send(Rest, i * StepUs);

            Check.That(world.Drops).IsEqualTo(1);
            Check.That(world.Diagnostics!.Trigger).IsEqualTo(DragDropTrigger.Settle);
        });
    }

    private sealed class World : IDisposable
    {
        internal World(SelectionMode mode = SelectionMode.ManualSelection)
        {
            Settings = new EyeCtrlSettings { SelectionMode = mode };
            Settings.Dwell.FixationRadiusPixels = 80;
            Settings.Dwell.DwellPauseGraceMs = 500;
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Trail.SetScreenBounds(Screen, 10, DragSummonEdges.Top | DragSummonEdges.Left);
            Trail.SetExitZones(new[] { new DragExitZone(Cross, ZoneRadiusPx, "cancel") });
            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
                Diagnostics = m.Diagnostics;
            });
            Messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

            // C2 fixup-01 R1: the release arms once the gaze has left its first spot after the start.
            Send(new PhysicalPoint(600, 2000), -StepUs);
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal int Drops { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }
        internal DragDropDiagnostics? Diagnostics { get; private set; }

        internal void Send(PhysicalPoint at, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(at, t));

        public void Dispose() => Trail.Dispose();
    }
}
