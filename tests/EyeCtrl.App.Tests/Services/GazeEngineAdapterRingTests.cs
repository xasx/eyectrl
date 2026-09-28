namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Ticket 7: the ring's own lifetime reaches the ViewModels through the adapter. The pin is Core's
/// accepted settle and the release is every exit from Fixation; both must be dispatched as their own
/// messages, independent of the activation button's show/hide (critique B1). Mutation: drop the
/// PinRing/ReleaseRing subscriptions in the adapter → no message arrives and both assertions red.
/// </summary>
public class GazeEngineAdapterRingTests
{
    [StaFact]
    public void RingPinAndRelease_AreForwarded_AsTheirOwnMessages()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);

        var messenger = new WeakReferenceMessenger();
        PinRingMessage? pinned = null;
        bool released = false;
        messenger.Register<PinRingMessage>(this, (_, message) => pinned = message);
        messenger.Register<ReleaseRingMessage>(this, (_, _) => released = true);

        using var adapter = new GazeEngineAdapter(
            engine, settings, messenger, Dispatcher.CurrentDispatcher,
            NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder());

        // The settle is accepted only in Fixation, so enter it first (the manager's own guard).
        engine.InteractionManager.OnFixationStarted(new PhysicalPoint(700, 600));
        engine.InteractionManager.OnFixationSettled(new PhysicalPoint(710, 610));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(pinned).IsNotNull();
        Check.That(pinned!.Position.X).IsEqualTo(710);
        Check.That(pinned.Position.Y).IsEqualTo(610);

        engine.InteractionManager.OnFixationEnded();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        Check.That(released).IsTrue();
    }

    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }
}
