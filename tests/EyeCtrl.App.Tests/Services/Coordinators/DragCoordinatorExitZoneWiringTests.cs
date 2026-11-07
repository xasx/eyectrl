namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// R1 (A4 fixup-02): the coordinator's exit-zone WIRING, through the real
/// <see cref="DragCoordinator.OnDragStarted"/>. The trail VM's zone check has its own tests; these
/// pin what production supplies: the Cancel zone in the upper slot, the Back zone in the lower
/// one, both centred on the drawn buttons, built from the same anchor the bar draws at.
///
/// <para>
/// S8 B11 (owner decision 07.11): each zone is TWICE the r50 button radius — 100 DIP, 200 px on
/// the owner's 200 % display — so a near miss of the ✕ never drops. The expected radius is written
/// here as a literal 2 × <see cref="UIConstants.ButtonRadius"/>, not read from the coordinator's
/// constant, so a changed factor fails.
/// </para>
/// <para>
/// DPI 1.0 and 2.0: the zone edge is checked 3 px INSIDE (no drop) and 3 px OUTSIDE (the settle
/// drop fires as before), so a zone that is missing, misplaced, shrunk or grown fails. The fake
/// trail overlay keeps the drag headless; no window is shown.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragCoordinatorExitZoneWiringTests
{
    /// <summary>The owner-decided no-drop zone: two r50 button radii.</summary>
    private static double ExpectedZoneRadius() => 2 * DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CoordinatorExitZones_AreTwiceTheDrawnButtons_AndEndExactlyAtTheirEdge(double scale)
    {
        // S8 B11 changed this pin from the drawn r50 to 2 × r50. Mutant M1 (factor 1.0) → the
        // 3 px-inside rows drop.
        DpiHelper.Initialize(scale, scale);
        try
        {
            var anchor = DragCoordinator.ExitAnchorPhysical;
            var cancel = DragCoordinator.CreateCancelButtonDefinition(600);
            var back = DragCoordinator.CreateBackButtonDefinition(600);
            var cancelCentre = anchor + new PhysicalPoint(0, cancel.VerticalOffset);
            var backCentre = anchor + new PhysicalPoint(0, back.VerticalOffset);
            var radius = ExpectedZoneRadius();

            // The drawn buttons themselves are unchanged: still r50.
            Check.That(cancel.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);
            Check.That(back.RadiusLogical).IsEqualTo(UIConstants.ButtonRadius);

            Check.WithCustomMessage($"Cancel centre dropped at DPI {scale}.")
                 .That(Drive(cancelCentre).Drops).IsEqualTo(0);
            Check.WithCustomMessage($"3 px inside Cancel's 2×r50 edge dropped at DPI {scale}.")
                 .That(Drive(new PhysicalPoint(cancelCentre.X - (radius - 3.0), cancelCentre.Y)).Drops)
                 .IsEqualTo(0);
            Check.WithCustomMessage($"Back centre dropped at DPI {scale}.")
                 .That(Drive(backCentre).Drops).IsEqualTo(0);
            Check.WithCustomMessage($"3 px inside Back's 2×r50 edge dropped at DPI {scale}.")
                 .That(Drive(new PhysicalPoint(backCentre.X - (radius - 3.0), backCentre.Y)).Drops)
                 .IsEqualTo(0);

            var outside = Drive(new PhysicalPoint(cancelCentre.X - (radius + 3.0), cancelCentre.Y));
            Check.WithCustomMessage(
                    $"3 px outside Cancel's 2×r50 edge did not drop at DPI {scale}: the zone is "
                    + "larger than two button radii.")
                 .That(outside.Drops).IsEqualTo(1);
            var outsideBack = Drive(new PhysicalPoint(backCentre.X - (radius + 3.0), backCentre.Y));
            Check.WithCustomMessage(
                    $"3 px outside Back's 2×r50 edge did not drop at DPI {scale}.")
                 .That(outsideBack.Drops).IsEqualTo(1);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Theory]
    [InlineData(145.0)]   // the owner's drag 10 ended 145 px from the ✕ centre
    [InlineData(150.0)]
    [InlineData(165.0)]
    [InlineData(190.0)]
    public void ANearMissOfTheCross_NeverDrops_OnTheOwnersDisplay(double distancePx)
    {
        // S8 B11 row 1: a look just up-left of „Ziehen abbrechen", at DPI 2.0 (r50 = 100 px, the
        // zone 200 px). Mutants: factor 1.0 (M1) drops every row; factor 1.5 (M2, a 150 px zone,
        // inclusive) drops the 165 and 190 px rows.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var cancel = DragCoordinator.CreateCancelButtonDefinition(600);
            var cancelCentre = DragCoordinator.ExitAnchorPhysical + new PhysicalPoint(0, cancel.VerticalOffset);
            var u = distancePx / Math.Sqrt(2);
            var nearMiss = new PhysicalPoint(cancelCentre.X - u, cancelCentre.Y - u);

            Check.WithCustomMessage($"a rest {distancePx} px up-left of the ✕ centre dropped the window.")
                 .That(Drive(nearMiss).Drops).IsEqualTo(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void ARestJustOutsideTheZone_DropsAtTheRest_OnTheOwnersDisplay()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var cancel = DragCoordinator.CreateCancelButtonDefinition(600);
            var cancelCentre = DragCoordinator.ExitAnchorPhysical + new PhysicalPoint(0, cancel.VerticalOffset);
            var u = (ExpectedZoneRadius() + 5.0) / Math.Sqrt(2);
            var justOutside = new PhysicalPoint(cancelCentre.X - u, cancelCentre.Y - u);

            var (drops, dropPoint) = Drive(justOutside);

            Check.That(drops).IsEqualTo(1);
            Check.That(dropPoint!.Value.DistanceTo(justOutside)).IsLessOrEqualThan(1e-6);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>
    /// Starts the REAL drag coordinator over the recording bar, then rests 900 ms at
    /// <paramref name="at"/>. Returns how many drop messages the trail fired.
    /// </summary>
    private static (int Drops, PhysicalPoint? DropPoint) Drive(PhysicalPoint at)
    {
        var input = new TestFakes.FakeInputSimulator();
        var (madeContext, _) = TestFakes.MakeDragContextWithBar(input);
        // The coordinator's DragDrop teardown is a deferred dispatcher callback; without one the
        // drop message itself would NRE. Nothing below pumps, so no teardown runs.
        var context = madeContext with
        {
            Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher,
        };
        var drag = new DragCoordinator
        {
            CreateTrailOverlay = static () => new TestFakes.FakeDragTrailOverlay(),
        };
        drag.Initialize(context);
        try
        {
            var drops = 0;
            PhysicalPoint? dropPoint = null;
            context.Messenger.Register<DragDropMessage>(context, (_, m) =>
            {
                drops++;
                dropPoint = m.DropPoint;
            });

            drag.OnDragStarted(new PhysicalPoint(300, 300));
            Check.That(drag.LastSetupFailure).IsNull();

            // C2 fixup-02 T3: the drop arms only once the gaze has left the spot of the first
            // sample after the start (the hidden Drag menu item). One saccade from far away
            // first, or the no-drop rows below would pass by never arming the release target.
            // drag-edge fixup-02: a tenth of the REAL screen in from the left, not anchor − 1600 px,
            // which is off-screen (an Exit, not an ordinary sample) on a screen under ~1700 px.
            var screen = ScreenGeometry.PrimaryScreenBoundsPhysical();
            var anchor = DragCoordinator.ExitAnchorPhysical;
            var arm = new PhysicalPoint(screen.X + (0.1 * screen.Width), anchor.Y);
            var leave = DragTrailViewModel.SameSpotRadii * context.DragTrailVM.ReleaseRadiusPhysical;
            Check.WithCustomMessage(
                    $"precondition: the rest ({at.X:F0}, {at.Y:F0}) is off the {screen.Width:F0}×{screen.Height:F0} px screen.")
                 .That(at.X >= screen.X && at.X <= screen.X + screen.Width
                       && at.Y >= screen.Y && at.Y <= screen.Y + screen.Height).IsTrue();
            Check.WithCustomMessage(
                    $"precondition: the arming sample ({arm.X:F0}, {arm.Y:F0}) is not a saccade away from the "
                    + $"rest on the {screen.Width:F0} px screen, or it lies in a drag exit zone.")
                 .That(arm.DistanceTo(at) > leave
                       && arm.DistanceTo(anchor) > DragCoordinator.ExitZoneRadiusPhysical
                          + Math.Abs(DragCoordinator.CancelButtonVerticalOffsetPhysical)).IsTrue();
            context.Messenger.Send(new GazeCursorMovedMessage(arm, -30_000));

            // 900 ms at 30 Hz: past the 500 ms settle and the 600 ms release dwell.
            for (long t = 0; t <= 900_000; t += 30_000)
                context.Messenger.Send(new GazeCursorMovedMessage(at, t));

            return (drops, dropPoint);
        }
        finally
        {
            drag.Dispose();
        }
    }
}
