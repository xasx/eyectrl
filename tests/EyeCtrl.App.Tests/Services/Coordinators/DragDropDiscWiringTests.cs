namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// C2: the drop disc's state reaches the overlay through the REAL
/// <see cref="DragCoordinator.OnDragStarted"/> wiring — its live hit radius (drawn = hit), the
/// release dwell's progress, and the grace-paused flag. The recording fake overlay keeps the drag
/// headless; no window is shown.
/// <para>
/// Each assertion compares the LAST disc the overlay was given with the view model's own state
/// after a sample that changes only that one property (the gaze holds still, so the cursor and the
/// centre raise nothing): a property the coordinator does not forward leaves the overlay a sample
/// behind.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DragDropDiscWiringTests
{
    private const long StepUs = 33_333;

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheOverlay_DrawsTheLiveRadius_TheProgress_AndThePausedHold(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        var input = new TestFakes.FakeInputSimulator();
        var (madeContext, _) = TestFakes.MakeDragContextWithBar(input);
        var context = madeContext with
        {
            Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher,
        };
        context.Settings.SelectionMode = SelectionMode.ManualSelection;
        var overlay = new TestFakes.FakeDragTrailOverlay();
        var drag = new DragCoordinator { CreateTrailOverlay = () => overlay };
        drag.Initialize(context);
        try
        {
            drag.OnDragStarted(new PhysicalPoint(300, 300));
            Check.That(drag.LastSetupFailure).IsNull();
            var vm = context.DragTrailVM;

            Check.WithCustomMessage("the drawn radius is not the detector's hit radius.")
                .That(overlay.Discs[^1].RadiusPhysical).IsEqualTo(vm.ReleaseRadiusPhysical);
            Check.That(vm.ReleaseRadiusPhysical).IsStrictlyGreaterThan(0);

            var spot = new PhysicalPoint(800, 600);
            var t = 0L;
            for (var i = 0; i < 10; i++, t += StepUs)
                context.Messenger.Send(new GazeCursorMovedMessage(spot, t));

            Check.That(vm.ReleaseProgress).IsStrictlyGreaterThan(0);
            Check.WithCustomMessage("the release progress did not reach the overlay.")
                .That(overlay.Discs[^1].Progress).IsEqualTo(vm.ReleaseProgress);

            // A crossing inside the spot: the hold is paused, then resumes on the next sample.
            var across = new PhysicalPoint(spot.X + (1.2 * vm.ReleaseRadiusPhysical), spot.Y);
            context.Messenger.Send(new GazeCursorMovedMessage(across, t));
            t += StepUs;
            Check.That(vm.IsReleasePaused).IsTrue();
            Check.That(overlay.Discs[^1].Paused).IsTrue();

            context.Messenger.Send(new GazeCursorMovedMessage(across, t));
            Check.That(vm.IsReleasePaused).IsFalse();
            Check.WithCustomMessage("the resumed (un-paused) state did not reach the overlay.")
                .That(overlay.Discs[^1].Paused).IsFalse();
            Check.That(overlay.Discs[^1]).IsEqualTo(vm.ReleaseDisc);
        }
        finally
        {
            drag.Dispose();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}
