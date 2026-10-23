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
        internal World()
        {
            Settings = new EyeCtrlSettings();
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

            Messenger.Register<DragDropMessage>(this, (_, _) => Drops++);
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

        internal void ShowCancel() =>
            Bar.ShowButtons(new[] { Cancel }, Anchor, EdgeBarOwner.Drag);

        internal void ShowBack() =>
            Bar.ShowButtons(new[] { Back }, Anchor, EdgeBarOwner.Drag);

        /// <summary>Feeds 30 Hz samples to BOTH surfaces for 800 ms, with the given ± jitter.</summary>
        internal void Drive(PhysicalPoint at, double jitter)
        {
            var n = 0;
            for (long t = 0; t <= HoldEndUs; t += StepUs, n++)
            {
                var dx = jitter == 0 ? 0 : (n % 2 == 0 ? jitter : -jitter);
                var dy = jitter == 0 ? 0 : (n % 3 == 0 ? jitter : -jitter);
                Messenger.Send(new GazeCursorMovedMessage(
                    new PhysicalPoint(at.X + dx, at.Y + dy), t));
            }
        }

        public void Dispose()
        {
            Trail.Dispose();
            Bar.Dispose();
        }
    }
}
