namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Wiring tests for the global ESC hatch subscriptions (review PR #142: "the wiring is
/// untested — FakeEscapeHatch.Press() was added and never called"). These drive the REAL
/// coordinators through the fake hatch and a real <see cref="Dispatcher"/>, so the
/// hatch→handler→guard→(no)message path is exercised end-to-end.
///
/// The positive paths (ESC mid-drag / mid-repeat) are NOT here on purpose: entering those
/// states through the coordinator constructs real overlay windows (DragTrailOverlay,
/// EdgeButtonBarWindow — concrete WPF types), which is manual-smoke territory. The guard
/// paths below still prove the subscription exists, fires, marshals, and decides.
/// </summary>
public class EscapeHatchWiringTests
{
    [StaFact]
    public void Drag_EscapeOutsideDragging_SendsNoCancel_AfterPumping()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var im = new InteractionManager();
        var dragVm = new DragTrailViewModel(new EyeCtrlSettings(), messenger);
        var hatch = new TestFakes.FakeEscapeHatch();
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            DragTrailVM = dragVm,
            EscapeHatch = hatch,
        };
        using var coordinator = new DragCoordinator();
        coordinator.Initialize(ctx);

        var cancelled = false;
        messenger.Register<object, DragCancelMessage>(this, (_, _) => cancelled = true);

        hatch.Press();
        Pump(dispatcher);

        Check.That(cancelled).IsFalse();
    }

    [StaFact]
    public void Repeat_EscapeOutsideRepeatMode_SendsNoExit_AfterPumping()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            EscapeHatch = hatch,
        };
        using var coordinator = new RepeatModeController();
        coordinator.Initialize(ctx);

        var exitRequested = false;
        messenger.Register<object, ExitRepeatModeMessage>(this, (_, _) => exitRequested = true);

        hatch.Press();
        Pump(dispatcher);

        Check.That(exitRequested).IsFalse();
    }

    /// <summary>Drains the dispatcher queue: run everything queued at/above Background priority.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
