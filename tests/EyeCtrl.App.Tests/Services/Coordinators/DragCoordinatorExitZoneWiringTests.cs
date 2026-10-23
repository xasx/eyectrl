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
/// one, both at the r50 hit radius, built from the same anchor the bar draws at.
///
/// <para>
/// DPI 1.0 and 2.0: the zone edge is checked 3 px INSIDE (no drop) and 3 px OUTSIDE (the settle
/// drop fires as before), so a zone that is missing, misplaced or shrunk fails. The fake trail
/// overlay keeps the drag headless; no window is shown.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragCoordinatorExitZoneWiringTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CoordinatorExitZones_MatchTheDrawnButtons_AndEndExactlyAtTheirEdge(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        try
        {
            var anchor = DragCoordinator.ExitAnchorPhysical;
            var cancel = DragCoordinator.CreateCancelButtonDefinition(600);
            var back = DragCoordinator.CreateBackButtonDefinition(600);
            var cancelCentre = anchor + new PhysicalPoint(0, cancel.VerticalOffset);
            var backCentre = anchor + new PhysicalPoint(0, back.VerticalOffset);
            var radius = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);

            Check.WithCustomMessage($"Cancel centre dropped at DPI {scale}.")
                 .That(Drive(cancelCentre).Drops).IsEqualTo(0);
            Check.WithCustomMessage($"3 px inside Cancel's r50 edge dropped at DPI {scale}.")
                 .That(Drive(new PhysicalPoint(cancelCentre.X - (radius - 3.0), cancelCentre.Y)).Drops)
                 .IsEqualTo(0);
            Check.WithCustomMessage($"Back centre dropped at DPI {scale}.")
                 .That(Drive(backCentre).Drops).IsEqualTo(0);

            var outside = Drive(new PhysicalPoint(cancelCentre.X - (radius + 3.0), cancelCentre.Y));
            Check.WithCustomMessage(
                    $"3 px outside Cancel's r50 edge did not drop at DPI {scale}: the zone is "
                    + "larger than the drawn button.")
                 .That(outside.Drops).IsEqualTo(1);
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
