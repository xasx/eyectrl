namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S8 B10 (sitting 07.11): the drop-time edge magnet. Windows maximizes a dragged window only when
/// the button is released AT the top edge (and half-snaps at the left/right edge), but the drop's
/// MouseUp moves the cursor to the rest centroid, which lands inside the screen almost always — so
/// window snap never worked by gaze. A drop whose point is within one drop-disc radius of the Top,
/// Left or Right edge now lands ON that edge; Bottom never (the taskbar, B9). In a corner only the
/// nearer edge snaps, a tie going to Top (owner: "top = maximize").
/// <para>
/// The owner's 3840×2400 screen; Manual Selection (the release dwell is the drop path) with the
/// owner's fixation radius of 80 px, so the disc radius r is 100 px at DPI 2.0 and 80 px at 1.0.
/// The owner's summon set (Top, Left) is published, as the coordinator does.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragEdgeMagnetTests
{
    private const long StepUs = 33_333;            // 30 Hz
    private const long DwellUs = 600_000;          // DragReleaseButtonDwellMs default

    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);

    public enum Edge { Top, Left, Right, Bottom }

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static PhysicalPoint Inset(Edge edge, double px) => edge switch
    {
        Edge.Top => new(1700, Screen.Y + px),
        Edge.Left => new(Screen.X + px, 1300),
        Edge.Right => new(Screen.X + Screen.Width - px, 1300),
        _ => new(1700, Screen.Y + Screen.Height - px),
    };

    private static PhysicalPoint OnEdge(Edge edge, PhysicalPoint rest) => edge switch
    {
        Edge.Top => new(rest.X, Screen.Y),
        Edge.Left => new(Screen.X, rest.Y),
        Edge.Right => new(Screen.X + Screen.Width, rest.Y),
        _ => new(rest.X, Screen.Y + Screen.Height),
    };

    [Theory]
    [InlineData(Edge.Top)]
    [InlineData(Edge.Left)]
    [InlineData(Edge.Right)]
    public void ARest90PxFromAMagnetEdge_DropsOnTheEdge_110PxDoesNot_OnTheOwnersDisplay(Edge edge)
    {
        // The ticket's pair at DPI 2.0 (r = 100 px). Mutants: no magnet (N1) → the 90 px rows stay;
        // no ≤ r check (N3) or width 2 r (N4) → the 110 px rows snap.
        WithDpi(2.0, () =>
        {
            var near = Inset(edge, 90);
            using (var world = new World())
            {
                Check.That(world.Trail.ReleaseRadiusPhysical).IsEqualTo(100.0);
                var drop = world.RestUntilDrop(near);
                Check.WithCustomMessage($"{edge}: a rest 90 px from the edge did not drop on it ({drop}).")
                     .That(drop).IsEqualTo(OnEdge(edge, near));
                Check.That(world.Diagnostics!.Magnet).IsEqualTo(Magnet(edge));
                Check.That(world.Diagnostics.ClampedPoint).IsEqualTo(near);
                Check.That(world.Diagnostics.RawPoint).IsEqualTo(near);
            }

            var far = Inset(edge, 110);
            using (var world = new World())
            {
                var drop = world.RestUntilDrop(far);
                Check.WithCustomMessage($"{edge}: a rest 110 px from the edge was pulled onto it ({drop}).")
                     .That(drop).IsEqualTo(far);
                Check.That(world.Diagnostics!.Magnet).IsEqualTo(DragMagnetEdges.None);
            }
        });
    }

    [Theory]
    [InlineData(Edge.Top)]
    [InlineData(Edge.Left)]
    [InlineData(Edge.Right)]
    public void TheMagnetWidth_IsOneDiscRadius_AtDpi1(Edge edge)
    {
        // DPI 1.0: r = 80 px (the owner's fixation radius over the 50 px floor). 0.9 r snaps, 1.1 r not.
        WithDpi(1.0, () =>
        {
            using (var world = new World())
            {
                var r = world.Trail.ReleaseRadiusPhysical;
                Check.That(r).IsEqualTo(80.0);
                var near = Inset(edge, 0.9 * r);
                Check.That(world.RestUntilDrop(near)).IsEqualTo(OnEdge(edge, near));
            }
            using (var world = new World())
            {
                var far = Inset(edge, 1.1 * world.Trail.ReleaseRadiusPhysical);
                Check.That(world.RestUntilDrop(far)).IsEqualTo(far);
            }
        });
    }

    [Fact]
    public void TheBottomEdge_NeverPulls()
    {
        // B9 ("not on the taskbar") stays PASS. Mutant N2 (Bottom in the magnet set) → RED.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var rest = Inset(Edge.Bottom, 50);
            Check.That(world.RestUntilDrop(rest)).IsEqualTo(rest);
            Check.That(world.Diagnostics!.Magnet).IsEqualTo(DragMagnetEdges.None);
        });
    }

    [Theory]
    [InlineData(80.0, 40.0, 80.0, 0.0)]    // nearer the top: Top only (maximize)
    [InlineData(40.0, 80.0, 0.0, 80.0)]    // nearer the left: Left only
    [InlineData(60.0, 60.0, 60.0, 0.0)]    // a tie goes to Top
    public void InACorner_OnlyTheNearerEdgeSnaps(double x, double y, double expectedX, double expectedY)
    {
        // Mutant N5 (both axes snapped) → (0, 0) in every row.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var drop = world.RestUntilDrop(new PhysicalPoint(x, y));
            Check.WithCustomMessage($"the corner rest ({x}, {y}) dropped at {drop}.")
                 .That(drop).IsEqualTo(new PhysicalPoint(expectedX, expectedY));
        });
    }

    [Fact]
    public void ATopRightCornerRest_SnapsTheNearerEdge()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var drop = world.RestUntilDrop(new PhysicalPoint(Screen.Width - 30, 70));
            Check.That(drop).IsEqualTo(new PhysicalPoint(Screen.Width, 70));
            Check.That(world.Diagnostics!.Magnet).IsEqualTo(DragMagnetEdges.Right);
        });
    }

    [Fact]
    public void AMidScreenDrop_IsUnchanged()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var rest = new PhysicalPoint(1920, 1200);
            Check.That(world.RestUntilDrop(rest)).IsEqualTo(rest);
            Check.That(world.Diagnostics!.Magnet).IsEqualTo(DragMagnetEdges.None);
        });
    }

    [Fact]
    public void TheAutoSelectionSettle_GetsTheSameMagnet()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World(SelectionMode.AutoSelection);
            var rest = Inset(Edge.Top, 90);
            Check.That(world.RestUntilDrop(rest)).IsEqualTo(new PhysicalPoint(rest.X, 0));
            Check.That(world.Diagnostics!.Trigger).IsEqualTo(DragDropTrigger.Settle);
            Check.That(world.Diagnostics.Magnet).IsEqualTo(DragMagnetEdges.Top);
        });
    }

    [Fact]
    public void WithNoPublishedBounds_TheDropIsUnchanged()
    {
        WithDpi(2.0, () =>
        {
            using var world = new World(publishBounds: false);
            var rest = Inset(Edge.Top, 50);
            Check.That(world.RestUntilDrop(rest)).IsEqualTo(rest);
            Check.That(world.Diagnostics!.Magnet).IsEqualTo(DragMagnetEdges.None);
        });
    }

    [Fact]
    public void TheMagnetWidth_FollowsTheLiveRadius_UnderAdaptationsGeometryMargin()
    {
        // The width is the disc as drawn (= hit, C2), so adaptation's margin widens it: +40 px makes
        // r 140 px, and a rest 120 px below the top snaps — at the 100 px settings radius it would not.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            world.Trail.DetectorForTest.SetEffectiveGeometry(extent => extent + 40);
            var rest = Inset(Edge.Top, 120);
            Check.That(world.RestUntilDrop(rest)).IsEqualTo(new PhysicalPoint(rest.X, 0));
            Check.That(world.Diagnostics!.DiscRadiusPhysical).IsEqualTo(140.0);
        });
    }

    [Fact]
    public void TheMagnetReadsTheRadiusAtTheDrop_BeforeTheTargetIsCleared()
    {
        // N6: read after ClearTargets, the live radius falls back to the drawn property, which is
        // one sample behind. A margin that arrives on the completing sample tells the two apart:
        // the hit radius at the drop is 140 px, the property still 100 px. Indistinguishable with no
        // margin change (the two are equal at every default).
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var rest = Inset(Edge.Top, 120);
            var t = 0L;
            for (var i = 0; i < 60 && world.Drops == 0; i++, t += StepUs)
            {
                if (world.Trail.ReleaseProgress + ((double)StepUs / DwellUs) >= 1.0)
                    world.Trail.DetectorForTest.SetEffectiveGeometry(extent => extent + 40);
                world.Send(rest, t);
            }

            Check.That(world.Drops).IsEqualTo(1);
            Check.WithCustomMessage("the magnet used the radius from before the drop sample.")
                 .That(world.DropPoint).IsEqualTo(new PhysicalPoint(rest.X, 0));
        });
    }

    private static DragMagnetEdges Magnet(Edge edge) => edge switch
    {
        Edge.Top => DragMagnetEdges.Top,
        Edge.Left => DragMagnetEdges.Left,
        Edge.Right => DragMagnetEdges.Right,
        _ => DragMagnetEdges.Bottom,
    };

    private sealed class World : IDisposable
    {
        internal World(SelectionMode mode = SelectionMode.ManualSelection, bool publishBounds = true)
        {
            Settings = new EyeCtrlSettings { SelectionMode = mode };
            Settings.Dwell.FixationRadiusPixels = 80;
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            if (publishBounds)
                Trail.SetScreenBounds(Screen, 10, DragSummonEdges.Top | DragSummonEdges.Left);
            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
                Diagnostics = m.Diagnostics;
            });
            Messenger.Send(new StartDragTrailMessage(new PhysicalPoint(1900, 1900)));

            // C2 fixup-01 R1: the release arms once the gaze has gone DragArmDistanceDip (600 px at
            // DPI 2.0) from its first spot after the start: this spot is over 1,000 px from every
            // rest below.
            Send(new PhysicalPoint(600, 2200), -StepUs);
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal int Drops { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }
        internal DragDropDiagnostics? Diagnostics { get; private set; }

        internal void Send(PhysicalPoint at, long t) =>
            Messenger.Send(new GazeCursorMovedMessage(at, t));

        /// <summary>Rests at <paramref name="at"/> at 30 Hz until the drop (≤ 2 s); returns the drop point.</summary>
        internal PhysicalPoint? RestUntilDrop(PhysicalPoint at)
        {
            for (var i = 0; i < 60 && Drops == 0; i++)
                Send(at, i * StepUs);
            Check.WithCustomMessage($"the rest at {at} never dropped.").That(Drops).IsEqualTo(1);
            return DropPoint;
        }

        public void Dispose() => Trail.Dispose();
    }
}
