namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owner Q1 = A (11.11): after „Ziehen", a rest may drop the window only once the gaze has gone
/// 300 DIP (600 px at the owner's 200 %) from where it rested when the drag started, or once the
/// drag is 2 s old. In the 6th sitting the latch armed at 150 px from that spot, and the first look
/// after the menu — 340–410 px to the right, inside the menu's vanished footprint — dropped the
/// window 0.94–1.48 s into 3 of 8 drags (drag-early-drop/report.md).
/// <para>
/// Manual Selection (the release dwell is the only drop), 30 Hz, the owner's fixation radius of
/// 80 px. Every timing row runs at the shipped 600 ms release dwell and at the ~1000 ms the owner
/// will set at prep: the dwell is read from the setting, never hard-coded.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class DragArmAwayTests
{
    private const long StepUs = 33_333; // 30 Hz

    /// <summary>The „Ziehen" item on the owner's screen-centred menu (6th sitting).</summary>
    private static readonly PhysicalPoint Item = new(1950, 740);

    [Theory]
    [InlineData(1.0, 300.0)]
    [InlineData(2.0, 600.0)]
    public void TheArmingDistance_Is300Dip(double scale, double expectedPx)
    {
        WithDpi(scale, () =>
            Check.That(DragTrailViewModel.DragArmDistancePhysical).IsCloseTo(expectedPx, 1e-9));
    }

    [Theory]
    [InlineData(2.0, 590.0, 600)]
    [InlineData(2.0, 590.0, 1000)]
    [InlineData(1.0, 290.0, 600)]
    [InlineData(1.0, 290.0, 1000)]
    public void JustShortOfTheDistance_BeforeTwoSeconds_ARestDoesNotDrop(double scale, double px, int dwellMs)
    {
        // A saccade to just under the distance at 0.1 s, then a rest there up to 1.95 s: longer than
        // the release dwell, but the drop is not armed. Mutant: the distance back to 150 px.
        WithDpi(scale, () =>
        {
            using var world = new World(dwellMs);
            var t = world.Linger(Item, 0, 100_000);
            var near = new PhysicalPoint(Item.X + px, Item.Y);
            world.Rest(near, t, 1_950_000);

            Check.WithCustomMessage($"DPI {scale}, dwell {dwellMs}: a rest {px} px from the item dropped at {world.DropAtUs} µs.")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Trail.ArmedBy).IsEqualTo(DragArmedBy.Never);
            Check.That(world.Trail.ArmMs).IsNull();
        });
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public void TheSixthSittingShape_TheFirstLookAfterTheMenu_DoesNotDrop(int dwellMs)
    {
        // The early drops' shape (drags 2, 4, 5): the gaze lingers on the item, and at 0.35 s looks
        // 350 px right, to where the menu was, and rests there to 1.5 s. Shipped (150 px), that rest
        // dropped the window at ~0.35 s + the dwell.
        WithDpi(2.0, () =>
        {
            using var world = new World(dwellMs);
            var t = world.Linger(Item, 0, 350_000);
            world.Rest(new PhysicalPoint(2300, 780), t, 1_500_000);

            Check.WithCustomMessage($"dwell {dwellMs}: the first look after the menu dropped at {world.DropAtUs} µs.")
                 .That(world.Drops).IsEqualTo(0);
        });
    }

    [Theory]
    [InlineData(2.0, 610.0, 600)]
    [InlineData(2.0, 610.0, 1000)]
    [InlineData(1.0, 310.0, 600)]
    [InlineData(1.0, 310.0, 1000)]
    public void PastTheDistance_ARestDropsAfterTheDwell_AtItsCentroid(double scale, double px, int dwellMs)
    {
        // The gaze lingers on the item to 0.5 s, then goes just past the distance and rests there,
        // wandering ±0.3 R. The drop is armed by distance at 0.5 s and comes one dwell later, at
        // the rest's centroid. Mutants: OR → AND (armed only at 2 s), the time fallback raised.
        WithDpi(scale, () =>
        {
            using var world = new World(dwellMs);
            var r = world.Trail.ReleaseRadiusPhysical;
            var t = world.Linger(Item, 0, 500_000);
            var spot = new PhysicalPoint(Item.X + px, Item.Y);
            var arrivedAt = t;
            var sent = new List<PhysicalPoint>();
            for (var k = 0; k < 120 && world.Drops == 0; k++, t += StepUs)
            {
                // The first sample is the spot itself, so the saccade lands just past the distance.
                var at = new PhysicalPoint(spot.X + (k % 2 == 0 ? 0 : 0.3 * r), spot.Y + (k % 3 == 0 ? 0 : -0.2 * r));
                sent.Add(at);
                world.Send(at, t);
            }

            Check.WithCustomMessage($"DPI {scale}: the rest {px} px from the item never dropped.").That(world.Drops).IsEqualTo(1);
            AssertDropOneDwellAfter(arrivedAt, world, dwellMs);
            var centroid = new PhysicalPoint(sent.Average(p => p.X), sent.Average(p => p.Y));
            Check.WithCustomMessage($"dropped at {world.DropPoint}, not the rest's centroid {centroid}.")
                 .That(world.DropPoint!.Value.DistanceTo(centroid)).IsLessOrEqualThan(1e-6);
            Check.That(world.Diagnostics!.ArmedBy).IsEqualTo(DragArmedBy.Distance);
            Check.That(world.Diagnostics.ArmMs).IsEqualTo((arrivedAt - world.FirstSampleUs) / 1000);
        });
    }

    [Theory]
    [InlineData(1.0, 600)]
    [InlineData(1.0, 1000)]
    [InlineData(2.0, 600)]
    [InlineData(2.0, 1000)]
    public void TwoHopsAwayFromTheItem_ArmByDistanceFromTheItem_NotFromTheFirstHop(double scale, int dwellMs)
    {
        // review-01 F1: the distance is measured from the drag's FIRST sample (the item), not from
        // whichever spot the gaze rests on. 175 DIP right at 0.35 s, a rest there until 0.8 s, then
        // 175 DIP more: the second hop is 350 DIP from the item and arms the drop, which comes one
        // release dwell later. Mutant N2: the anchor re-taken at each new spot while disarmed sees
        // only 175 DIP and waits for the 2 s fallback.
        WithDpi(scale, () =>
        {
            using var world = new World(dwellMs);
            var hop = DpiHelper.LogicalToPhysicalX(175);
            var t = world.Linger(Item, 0, 350_000);
            t = world.Linger(new PhysicalPoint(Item.X + hop, Item.Y), t, 800_000);
            Check.That(world.Drops).IsEqualTo(0);
            Check.That(world.Trail.ArmedBy).IsEqualTo(DragArmedBy.Never);

            var secondHopAt = t;
            var far = new PhysicalPoint(Item.X + (2 * hop), Item.Y);
            world.Rest(far, t, secondHopAt + (dwellMs * 1000L) + (4 * StepUs));

            Check.WithCustomMessage($"DPI {scale}: the rest {2 * hop} px from the item never dropped.").That(world.Drops).IsEqualTo(1);
            AssertDropOneDwellAfter(secondHopAt, world, dwellMs);
            Check.That(world.DropPoint).IsEqualTo(far);
            Check.That(world.Diagnostics!.ArmedBy).IsEqualTo(DragArmedBy.Distance);
            Check.That(world.Diagnostics.ArmMs).IsEqualTo((secondHopAt - world.FirstSampleUs) / 1000);
        });
    }

    [Theory]
    [InlineData(1.0, 600)]
    [InlineData(1.0, 1000)]
    [InlineData(2.0, 600)]
    [InlineData(2.0, 1000)]
    public void WithNoTravel_TheDropArmsAtTwoSeconds_AndDropsOneDwellLater(double scale, int dwellMs)
    {
        // The gaze never leaves the item (a target right next to it): the drop arms on the first
        // sample at 2.0 s and the rest, running since 0, counts only from there. Mutants: no time
        // fallback (never drops), 2000 → 3000 (drops a second late), arming credits the earlier
        // rest (drops at 2.0 s).
        WithDpi(scale, () =>
        {
            using var world = new World(dwellMs);
            var t = world.Linger(Item, 0, 2_000_000);
            Check.WithCustomMessage($"dropped at {world.DropAtUs} µs, before the 2 s fallback.").That(world.Drops).IsEqualTo(0);
            var armedAt = t;
            world.Rest(Item, t, armedAt + (dwellMs * 1000L) + (4 * StepUs));

            Check.WithCustomMessage("the rest on the item never dropped after 2 s.").That(world.Drops).IsEqualTo(1);
            AssertDropOneDwellAfter(armedAt, world, dwellMs);
            Check.That(world.DropPoint).IsEqualTo(Item);
            Check.That(world.Diagnostics!.ArmedBy).IsEqualTo(DragArmedBy.Time);
            Check.That(world.Diagnostics.ArmMs).IsEqualTo((armedAt - world.FirstSampleUs) / 1000);
        });
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public void ARestThatStartedBeforeTwoSeconds_CountsOnlyFromArming(int dwellMs)
    {
        // The gaze lingers on the item to 1.5 s, then moves 200 px (a new spot, still short of the
        // distance) and rests. Its dwell must count from 2.0 s, not from 1.5 s: the drop comes one
        // dwell after arming. Mutant: arming credits the rest already running (drops at 1.5 s + the
        // dwell).
        WithDpi(2.0, () =>
        {
            using var world = new World(dwellMs);
            var t = world.Linger(Item, 0, 1_500_000);
            var spot = new PhysicalPoint(Item.X + 200, Item.Y + 40);
            t = world.Linger(spot, t, 2_000_000);
            Check.That(world.Drops).IsEqualTo(0);
            var armedAt = t;
            world.Rest(spot, t, armedAt + (dwellMs * 1000L) + (4 * StepUs));

            Check.WithCustomMessage("the rest never dropped after 2 s.").That(world.Drops).IsEqualTo(1);
            AssertDropOneDwellAfter(armedAt, world, dwellMs);
            Check.That(world.DropPoint).IsEqualTo(spot);
            Check.That(world.Diagnostics!.ArmedBy).IsEqualTo(DragArmedBy.Time);
        });
    }

    [Fact]
    public void ANewDrag_StartsDisarmed_WithItsOwnClock()
    {
        // The trail view model is a DI singleton: a second drag starts disarmed and its 2 s run
        // from its own first sample, not the first drag's.
        WithDpi(2.0, () =>
        {
            using var world = new World(600);
            world.Linger(Item, 0, 2_100_000);
            Check.That(world.Trail.ArmedBy).IsEqualTo(DragArmedBy.Time);

            world.Messenger.Send(new StartDragTrailMessage(new PhysicalPoint(1000, 600)));
            Check.That(world.Trail.ArmedBy).IsEqualTo(DragArmedBy.Never);
            Check.That(world.Trail.ArmMs).IsNull();
            world.Rest(Item, 5_000_000, 6_900_000);
            Check.WithCustomMessage($"the second drag dropped at {world.DropAtUs} µs, on the first drag's clock.")
                 .That(world.Drops).IsEqualTo(0);
        });
    }

    /// <summary>The drop came one release dwell after <paramref name="fromUs"/>, within a sample.</summary>
    private static void AssertDropOneDwellAfter(long fromUs, World world, int dwellMs)
    {
        var after = world.DropAtUs!.Value - fromUs;
        var dwellUs = dwellMs * 1000L;
        Check.WithCustomMessage($"dwell {dwellMs}: the drop came {after} µs after arming, before the dwell.")
             .That(after).IsGreaterOrEqualThan(dwellUs - StepUs);
        Check.WithCustomMessage($"dwell {dwellMs}: the drop came {after} µs after arming, past the dwell plus two samples.")
             .That(after).IsLessOrEqualThan(dwellUs + (2 * StepUs));
    }

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private sealed class World : IDisposable
    {
        internal World(int dwellMs)
        {
            Settings = new EyeCtrlSettings { SelectionMode = SelectionMode.ManualSelection };
            Settings.Dwell.FixationRadiusPixels = 80;
            Settings.Dwell.DragReleaseButtonDwellMs = dwellMs;
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
                Diagnostics = m.Diagnostics;
                DropAtUs = LastSentUs;
            });
            // The grab point is a menu-ring away from the item, as in every sitting drag.
            Messenger.Send(new StartDragTrailMessage(new PhysicalPoint(2600, 700)));
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal int Drops { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }
        internal DragDropDiagnostics? Diagnostics { get; private set; }
        internal long? DropAtUs { get; private set; }
        internal long? FirstSampleUs { get; private set; }
        private long LastSentUs { get; set; }

        internal void Send(PhysicalPoint at, long t)
        {
            FirstSampleUs ??= t;
            LastSentUs = t;
            Messenger.Send(new GazeCursorMovedMessage(at, t));
        }

        /// <summary>
        /// Samples <paramref name="at"/> from <paramref name="fromUs"/> while t is below
        /// <paramref name="untilUs"/>, stopping at a drop; returns the next sample's time.
        /// </summary>
        internal long Linger(PhysicalPoint at, long fromUs, long untilUs)
        {
            var t = fromUs;
            for (; t < untilUs && Drops == 0; t += StepUs)
                Send(at, t);
            return t;
        }

        /// <summary>As <see cref="Linger"/>, up to and including <paramref name="untilUs"/>.</summary>
        internal void Rest(PhysicalPoint at, long fromUs, long untilUs) => Linger(at, fromUs, untilUs + 1);

        public void Dispose() => Trail.Dispose();
    }
}
