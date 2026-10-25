namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// A5 fixup-03 H2, the production wiring half: entering repeat mode publishes the primary screen's
/// physical bounds (plus the shared tolerance) as the arm gate, and a fixation past the edge arms
/// nothing. The Core contract has its own tests with explicit bounds; this one proves the
/// controller actually publishes them — the dead-feature trap the F2 zone fell into.
/// </summary>
public class RepeatModeOffScreenTests
{
    private const long Ms = 1_000;

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void RepeatEntry_PublishesThePrimaryScreenBounds_AndRefusesPastEdgeArms()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var input = new TestFakes.FakeInputSimulator();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var im = new InteractionManager();
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(edgeButtonBar: new TestFakes.FakeEdgeButtonBar()),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
        };
        using var controller = new RepeatModeController();
        controller.Initialize(ctx);

        im.EnterRepeatMode(InteractionAction.LeftClick);
        Pump(dispatcher);   // the controller's entry is posted from StateChanged
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);

        var published = im.RepeatScreenBoundsForTest;
        Check.That(published).IsNotNull();
        Check.That(published!.Value.Tolerance).IsEqualTo(ScreenGeometry.OffScreenTolerancePhysical);
        Check.That(published.Value.Bounds.X).IsEqualTo(0);
        Check.That(published.Value.Bounds.Y).IsEqualTo(0);
        Check.That(published.Value.Bounds.Width)
             .IsEqualTo(DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth));
        Check.That(published.Value.Bounds.Height)
             .IsEqualTo(DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight));

        var bounds = published.Value.Bounds;
        var fired = new List<InteractionAction>();
        im.ActionRequested += (a, _, _) => fired.Add(a);
        const long nowUs = 10 * Ms;
        im.NoteGazeSample(true, nowUs);

        // The summon gesture past the top edge: no arm, no click.
        im.OnFixationStarted(new PhysicalPoint(bounds.X + (bounds.Width / 2), bounds.Y - 100));
        im.NoteGazeSample(true, nowUs + (2_000 * Ms));
        Check.WithCustomMessage("an off-screen fixation armed the repeat dwell in production wiring.")
             .That(fired).IsEmpty();

        // T1: the published TOLERANCE is load-bearing. 5 px above the edge is still inside it, so
        // this fixation arms and fires (a controller that published tolerance 0 would fail here).
        im.OnFixationEnded();
        im.OnFixationStarted(new PhysicalPoint(bounds.X + (bounds.Width / 2), bounds.Y - 5));
        im.NoteGazeSample(true, nowUs + (6_000 * Ms));
        Check.WithCustomMessage(
                "a fixation 5 px past the edge (inside the published tolerance) did not arm.")
             .That(fired).HasSize(1);

        // The on-screen half still fires.
        im.OnFixationEnded();
        im.OnFixationStarted(new PhysicalPoint(
            bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        im.NoteGazeSample(true, nowUs + (8_000 * Ms));
        Check.That(fired).HasSize(2);

        // Leaving the mode retracts the gate with the suppression zones.
        im.ExitRepeatMode();
        Pump(dispatcher);
        Check.That(im.RepeatScreenBoundsForTest).IsNull();
    }
}
