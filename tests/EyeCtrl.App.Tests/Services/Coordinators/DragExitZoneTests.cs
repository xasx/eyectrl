namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// PX1 (A4 fixup-01): at default settings the drag's settle drop beat its own Cancel button —
/// resting on the r50 Cancel dropped the item after ~533 ms, before Cancel's 600 ms dwell could
/// fire. The coordinator now supplies the edge-bar drag buttons' hit circles to the trail VM;
/// while the gaze is inside one, neither the settle nor the gaze-following release target
/// progresses, so the button's own dwell is the only thing that fires.
///
/// <para>
/// Runs at DPI 2.0 (the owner's scale), 30 Hz, with jitter 0 and ±3 px. The real
/// <see cref="DragTrailViewModel"/> and <see cref="EdgeButtonBarViewModel"/> receive the same
/// samples; the bar's Cancel/Back activate through the real messenger. The zones come from the
/// SAME anchor the bar draws at, as production does.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragExitZoneTests
{
    private const long StepUs = 33_333; // 30 Hz
    private const long HoldEndUs = 800_000;

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    public void ARestOnCancel_FiresCancel_AndNeverDrops(double jitter)
    {
        WithDpi(2.0, () =>
        {
            using var world = new World();
            world.ShowCancel();
            world.Drive(world.CancelCentre, jitter);

            Check.WithCustomMessage(
                    $"resting on Cancel dropped the item (drops={world.Drops}, "
                    + $"cancels={world.Cancels}, jitter={jitter}).")
                 .That(world.Drops).IsEqualTo(0);
            Check.WithCustomMessage("Cancel did not fire on a rest inside its own hit circle.")
                 .That(world.Cancels).IsEqualTo(1);
            Check.That(world.Trail.IsDragActive).IsFalse();
        });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    public void ARestOnBack_FiresBack_AndNeverDrops(double jitter)
    {
        WithDpi(2.0, () =>
        {
            using var world = new World();
            world.ShowBack();
            world.Drive(world.BackCentre, jitter);

            Check.WithCustomMessage(
                    $"resting on Back dropped the item (drops={world.Drops}, "
                    + $"dismisses={world.Dismisses}, jitter={jitter}).")
                 .That(world.Drops).IsEqualTo(0);
            Check.WithCustomMessage("Back did not fire on a rest inside its own hit circle.")
                 .That(world.Dismisses).IsEqualTo(1);
        });
    }

    [Fact]
    public void AShorterReleaseDwell_StillLosesToCancel()
    {
        // R3: with the release target's dwell SHORTER than Cancel's (a legal settings pair), an
        // unfiltered release target would fire on the Cancell rest at 400 ms and drop the item
        // before Cancel's 600 ms. The exit-zone filter is what keeps Cancel the winner.
        WithDpi(2.0, () =>
        {
            using var world = new World(s => s.Dwell.DragReleaseButtonDwellMs = 400);
            world.ShowCancel();
            world.Drive(world.CancelCentre, jitter: 0);

            Check.WithCustomMessage(
                    $"release 400 beat Cancel 600 (drops={world.Drops}, cancels={world.Cancels}).")
                 .That(world.Drops).IsEqualTo(0);
            Check.That(world.Cancels).IsEqualTo(1);
        });
    }

    [Fact]
    public void EnteringAnExit_ResetsTheSettleProgress()
    {
        // P6: a settle that had already begun off the bar must not sit frozen behind the exit.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            var offBar = new PhysicalPoint(world.CancelCentre.X - 400, world.CancelCentre.Y);
            world.DriveRange(offBar, 0, 300_000);
            Check.That(world.Trail.SettleProgress).IsStrictlyGreaterThan(0);

            world.DriveRange(world.CancelCentre, 330_000, 420_000);

            Check.WithCustomMessage(
                    $"the settle carried into the exit zone (progress {world.Trail.SettleProgress:0.###}).")
                 .That(world.Trail.SettleProgress).IsEqualTo(0);
        });
    }

    [Fact]
    public void ASaccadeToANewSpot_NeedsTheFullReleaseDwellThere()
    {
        // PX2/R4: a held release dwell must not follow a saccade. The settle is pushed out of the
        // window so the release target is the only drop path being measured.
        WithDpi(2.0, () =>
        {
            using var world = new World(s => s.Drag.SettleDwellMs = 2000);
            var p = new PhysicalPoint(world.CancelCentre.X - 400, world.CancelCentre.Y);
            var q = new PhysicalPoint(p.X - 500, p.Y);

            world.DriveRange(p, 0, 450_000);          // 450 ms at P
            world.DriveRange(q, 480_000, 990_000);    // 510 ms at Q — under the full dwell

            Check.WithCustomMessage(
                    $"carried release progress dropped the item at Q after only "
                    + $"{990_000 - 480_000} ms (drops={world.Drops}).")
                 .That(world.Drops).IsEqualTo(0);

            world.DriveRange(q, 1_020_000, 1_200_000); // past the full dwell at Q

            Check.That(world.Drops).IsEqualTo(1);
            Check.That(world.DropPoint!.Value.X).IsCloseTo(q.X, 1.0);
            Check.That(world.DropPoint!.Value.Y).IsCloseTo(q.Y, 1.0);
        });
    }

    [Fact]
    public void ABlinkAtTheSameSpot_ResumesTheReleaseDwell()
    {
        // The reset must NOT swallow A1's grace: a blink or a short gap at the same spot keeps
        // the release progress.
        WithDpi(2.0, () =>
        {
            using var world = new World(s => s.Drag.SettleDwellMs = 2000);
            var p = new PhysicalPoint(world.CancelCentre.X - 400, world.CancelCentre.Y);

            world.DriveRange(p, 0, 450_000);
            world.DriveGap(p, 480_000, 660_000);      // 200 ms blink, same spot
            world.DriveRange(p, 690_000, 750_000);    // 60 ms after the blink

            Check.WithCustomMessage(
                    $"the blink lost the held progress (drops={world.Drops} after 60 ms).")
                 .That(world.Drops).IsEqualTo(0);

            world.DriveRange(p, 780_000, 1_000_000);  // ~310 ms after the blink

            Check.WithCustomMessage(
                    "the release dwell did not resume after the blink (a clean restart would need "
                    + "600 ms from the return, i.e. past 1_290_000).")
                 .That(world.Drops).IsEqualTo(1);
        });
    }

    [Fact]
    public void AVisitToAnExit_ResetsTheReleaseDwell()
    {
        // R4: looking at Cancel mid-rest is a clean break; the return to P needs a full dwell.
        WithDpi(2.0, () =>
        {
            using var world = new World(s => s.Drag.SettleDwellMs = 2000);
            var p = new PhysicalPoint(world.CancelCentre.X - 400, world.CancelCentre.Y);

            world.DriveRange(p, 0, 450_000);             // 450 ms at P
            world.DriveRange(world.CancelCentre, 480_000, 570_000); // 90 ms on Cancel
            world.DriveRange(p, 600_000, 900_000);       // 300 ms back at P

            Check.WithCustomMessage(
                    $"the Cancel visit let the release progress carry back (drops={world.Drops} "
                    + "after 300 ms).")
                 .That(world.Drops).IsEqualTo(0);

            world.DriveRange(p, 930_000, 1_300_000);     // past the full dwell after the return

            Check.That(world.Drops).IsEqualTo(1);
            Check.That(world.DropPoint!.Value.X).IsCloseTo(p.X, 1.0);
        });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    public void ARestOneCentimetreLeftOfTheBar_StillDrops(double jitter)
    {
        // The suppression is the buttons' hit circles, not the whole screen edge: a rest just
        // outside them must keep today's settle-drop behaviour.
        WithDpi(2.0, () =>
        {
            using var world = new World();
            world.ShowCancel();
            var oneCm = DpiHelper.LogicalToPhysicalX(37.8); // 1 cm at 96 DPI, scaled
            var left = new PhysicalPoint(
                world.CancelCentre.X - world.ExitRadius - oneCm,
                world.CancelCentre.Y);
            world.Drive(left, jitter);

            Check.WithCustomMessage(
                    $"a rest 1 cm left of the bar did not drop (drops={world.Drops}, jitter={jitter}).")
                 .That(world.Drops).IsEqualTo(1);
            Check.That(world.Cancels).IsEqualTo(0);
        });
    }

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>
    /// Both production view models over one messenger, with the drag's start and the edge-bar
    /// claim wired exactly as the app does (same anchor, same definitions, zones from that anchor).
    /// </summary>
    private sealed class World : IDisposable
    {
        internal World(Action<EyeCtrlSettings>? configure = null)
        {
            Settings = new EyeCtrlSettings();
            configure?.Invoke(Settings);
            Messenger = new WeakReferenceMessenger();
            Trail = new DragTrailViewModel(Settings, Messenger);
            Bar = new EdgeButtonBarViewModel(Settings, Messenger);

            Anchor = DragCoordinator.ExitAnchorPhysical;
            Cancel = DragCoordinator.CreateCancelButtonDefinition(
                Settings.Dwell.DragRightEdgeButtonDwellMs);
            Back = DragCoordinator.CreateBackButtonDefinition(
                Settings.Dwell.DragRightEdgeButtonDwellMs);
            CancelCentre = Anchor + new PhysicalPoint(0, Cancel.VerticalOffset);
            BackCentre = Anchor + new PhysicalPoint(0, Back.VerticalOffset);
            ExitRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

            Trail.SetExitZones(new[]
            {
                new DragExitZone(CancelCentre, ExitRadius),
                new DragExitZone(BackCentre, ExitRadius),
            });

            Messenger.Register<DragDropMessage>(this, (_, m) =>
            {
                Drops++;
                DropPoint = m.DropPoint;
            });
            Messenger.Register<DragCancelMessage>(this, (_, _) => Cancels++);
            Messenger.Register<DragDismissManualSelectionMessage>(this, (_, _) => Dismisses++);
            Messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));
        }

        internal EyeCtrlSettings Settings { get; }
        internal WeakReferenceMessenger Messenger { get; }
        internal DragTrailViewModel Trail { get; }
        internal EdgeButtonBarViewModel Bar { get; }
        internal PhysicalPoint Anchor { get; }
        internal EdgeButtonDefinition Cancel { get; }
        internal EdgeButtonDefinition Back { get; }
        internal PhysicalPoint CancelCentre { get; }
        internal PhysicalPoint BackCentre { get; }
        internal double ExitRadius { get; }
        internal int Drops { get; private set; }
        internal int Cancels { get; private set; }
        internal int Dismisses { get; private set; }
        internal PhysicalPoint? DropPoint { get; private set; }

        internal void ShowCancel() =>
            Bar.ShowButtons(new[] { Cancel }, Anchor, EdgeBarOwner.Drag);

        internal void ShowBack() =>
            Bar.ShowButtons(new[] { Back }, Anchor, EdgeBarOwner.Drag);

        /// <summary>Feeds 30 Hz samples to BOTH surfaces for 800 ms, with the given ± jitter.</summary>
        internal void Drive(PhysicalPoint at, double jitter) =>
            DriveRange(at, 0, HoldEndUs, jitter);

        /// <summary>Feeds 30 Hz valid samples in [fromUs, toUs].</summary>
        internal void DriveRange(PhysicalPoint at, long fromUs, long toUs, double jitter = 0)
        {
            var n = 0;
            for (long t = fromUs; t <= toUs; t += StepUs, n++)
            {
                var dx = jitter == 0 ? 0 : (n % 2 == 0 ? jitter : -jitter);
                var dy = jitter == 0 ? 0 : (n % 3 == 0 ? jitter : -jitter);
                Messenger.Send(new GazeCursorMovedMessage(
                    new PhysicalPoint(at.X + dx, at.Y + dy), t));
            }
        }

        /// <summary>Feeds 30 Hz gap-held (blink) samples in [fromUs, toUs].</summary>
        internal void DriveGap(PhysicalPoint at, long fromUs, long toUs)
        {
            for (long t = fromUs; t <= toUs; t += StepUs)
                Messenger.Send(new GazeCursorMovedMessage(new GazeFrame(at, t, IsInGap: true)));
        }

        public void Dispose()
        {
            Trail.Dispose();
            Bar.Dispose();
        }
    }
}
