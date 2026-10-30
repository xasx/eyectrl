namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// C2 fixup-01: the ring-team review's pins (C2 ring r01, adopted as written). Window-free
/// (DragTrailViewModel only). The linger pin is R1's (the leave-before-arm latch), the glide pin is
/// the same-spot bound's (R4: SameSpotRadii widened survived every other row), and the adaptation
/// pins are drawn = hit under adaptation's geometry routing (R4).
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragDropTargetReviewPins
{
    private const long StepUs = 33_333;

    private sealed class World : IDisposable
    {
        public World(PhysicalPoint grab)
        {
            var s = new EyeCtrlSettings();
            s.SelectionMode = SelectionMode.ManualSelection;
            s.Dwell.FixationRadiusPixels = 80;
            s.Dwell.DwellPauseGraceMs = 500;
            M = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(s, M);
            M.Register<DragDropMessage>(this, (_, m) => { Drops++; DropPoint = m.DropPoint; });
            M.Send(new StartDragTrailMessage(grab));
        }
        public WeakReferenceMessenger M { get; }
        public DragTrailViewModel Trail { get; }
        public int Drops { get; private set; }
        public PhysicalPoint? DropPoint { get; private set; }
        public void Send(PhysicalPoint p, long t) => M.Send(new GazeCursorMovedMessage(p, t));
        public void Dispose() => Trail.Dispose();
    }

    private static void At200(Action body)
    {
        DpiHelper.Initialize(2.0, 2.0);
        try { body(); } finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void ASteadyGlideOfMoreThanTwoRadiiPerDwell_NeverDrops()
    {
        // The drift tolerance is bounded by the dwell's SPOT: a glide carries the dwell for about
        // 2 R of travel and no further. At 0.12 R per sample (12 px at R = 100, 360 px/s) that is
        // under the 600 ms dwell, so no direction may drop. Kills SameSpotRadii widened (M9).
        At200(() =>
        {
            for (var d = 0; d < 8; d++)
            {
                using var w = new World(new PhysicalPoint(600, 600));
                var r = w.Trail.ReleaseRadiusPhysical;
                var a = d * Math.PI / 4;
                var s = new PhysicalPoint(1900, 1200);
                for (var i = 0; i < 90; i++)
                    w.Send(new PhysicalPoint(s.X + (i * 0.12 * r * Math.Cos(a)), s.Y + (i * 0.12 * r * Math.Sin(a))), i * StepUs);
                Check.WithCustomMessage($"a {0.12 * r} px/sample glide at {d * 45}° dropped mid-glide.")
                    .That(w.Drops).IsEqualTo(0);
            }
        });
    }

    [Fact]
    public void TheDrawnRadius_FollowsAdaptationsGeometryMargin()
    {
        // "Drawn = hit" under adaptation's geometry routing: the disc must read the LIVE radius.
        // Kills M13 (radius read from settings).
        At200(() =>
        {
            using var w = new World(new PhysicalPoint(600, 600));
            var baseR = w.Trail.ReleaseRadiusPhysical;
            w.Trail.DetectorForTest.SetEffectiveGeometry(extent => extent + 40);
            w.Send(new PhysicalPoint(900, 700), 0);
            Check.That(w.Trail.ReleaseRadiusPhysical).IsCloseTo(baseR + 40, 1e-9);
            Check.That(w.Trail.ReleaseDisc.RadiusPhysical).IsCloseTo(baseR + 40, 1e-9);
        });
    }

    [Fact]
    public void TheDrawnRadius_FollowsAMidDragGeometryMargin_OnTheNextSample()
    {
        // Fixup-01 R4: the margin can change while the drag runs; the drawn disc follows on the
        // very next sample, not at the next drag.
        At200(() =>
        {
            using var w = new World(new PhysicalPoint(600, 600));
            var baseR = w.Trail.ReleaseRadiusPhysical;
            w.Send(new PhysicalPoint(900, 700), 0);
            Check.That(w.Trail.ReleaseRadiusPhysical).IsCloseTo(baseR, 1e-9);

            w.Trail.DetectorForTest.SetEffectiveGeometry(extent => extent + 20);
            w.Send(new PhysicalPoint(905, 700), StepUs);
            Check.That(w.Trail.ReleaseRadiusPhysical).IsCloseTo(baseR + 20, 1e-9);
            Check.That(w.Trail.ReleaseDisc.RadiusPhysical).IsCloseTo(baseR + 20, 1e-9);
        });
    }

    [Fact]
    public void ALingerOnTheDragMenuItem_AfterTheDragStarts_NeverDrops_ThenARestElsewhereDoes()
    {
        // F1: the drag starts from the radial menu's Drag item, ~150 DIP from the grab; the gaze
        // is still resting there. A rest there must not drop the item onto the menu spot. Once
        // the gaze has left and rests on the destination, the drop works as usual.
        At200(() =>
        {
            using var w = new World(new PhysicalPoint(1000, 1000));
            var r = w.Trail.ReleaseRadiusPhysical;
            var menu = new PhysicalPoint(1300, 1000);
            var rng = new Random(11);
            var at = menu;
            long t = 0;
            for (var i = 0; i < 60; i++, t += StepUs)
            {
                var next = new PhysicalPoint(at.X + ((rng.NextDouble() - 0.5) * 0.3 * r), at.Y + ((rng.NextDouble() - 0.5) * 0.3 * r));
                if (next.DistanceTo(menu) <= 0.6 * r) at = next;
                w.Send(at, t);
            }
            Check.WithCustomMessage($"the item dropped on the menu spot {w.DropPoint} during a 2 s linger after the drag started.")
                .That(w.Drops).IsEqualTo(0);

            var dest = new PhysicalPoint(1900, 1500);
            for (var i = 0; i < 30 && w.Drops == 0; i++, t += StepUs)
                w.Send(dest, t);
            Check.That(w.Drops).IsEqualTo(1);
            Check.That(w.DropPoint).IsEqualTo(dest);
        });
    }
}
