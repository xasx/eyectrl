namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8's offer coordinator: the geometry hand-off, the chrome-zone publication on every trigger
/// the plan lists, and the external-dwell teardown. The zone collector is injected, so these exercise
/// the coordinator's subscriptions and the push into <see cref="InteractionManager"/> without standing
/// up the keyboard/menu windows; the pure composition is pinned in
/// <see cref="ActivationChromeZonesTests"/> and the production collector is a thin read on top of it.
/// </summary>
public class ActivationOfferCoordinatorTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);

    private sealed record Setup(
        ActivationOfferCoordinator Coordinator,
        EventCoordinationContext Context,
        InteractionManager InteractionManager,
        ActivationOfferEngine Engine,
        WeakReferenceMessenger Messenger,
        Dispatcher Dispatcher,
        TestFakes.FakeGazeProvider Provider);

    private static Setup Build(Func<EventCoordinationContext, PhysicalRect[]>? collect = null)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var provider = new TestFakes.FakeGazeProvider();
        var gazeEngine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        var im = gazeEngine.InteractionManager;
        var engine = new ActivationOfferEngine(Screen, scale: 2.0, ringSizeDip: 250, dwellMs: 700, fixationRadiusPx: 90);
        im.AttachActivationOffers(engine);

        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            GazeEngine = gazeEngine,
            InteractionManager = im,
            Settings = settings,
            Messenger = messenger,
            Dispatcher = Dispatcher.CurrentDispatcher,
            KeyboardVM = new KeyboardViewModel(settings, messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
        };

        var coordinator = new ActivationOfferCoordinator(
            new ToolbarLifecycleCoordinator(new ModalInteractionState()),
            collect ?? (_ => Array.Empty<PhysicalRect>()));
        coordinator.Initialize(context);
        Pump();
        return new Setup(coordinator, context, im, engine, messenger, Dispatcher.CurrentDispatcher, provider);
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

    [StaFact]
    public void CollectedZones_ReachTheOfferEngine_AndAChangeRepublishesThem()
    {
        // The zones are plain data pushed to the engine: a settle inside one makes no offer, and once
        // the next trigger publishes an empty set the same settle places again. Mutation: push the
        // zones to anything but the engine (or drop the initial publish) → the in-zone settle offers.
        var zones = new List<PhysicalRect> { new(400, 400, 200, 200) };
        var s = Build(_ => zones.ToArray());
        using var _ = s.Context.GazeEngine;

        s.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        s.InteractionManager.OnFixationSettled(new PhysicalPoint(500, 500));
        Check.WithCustomMessage("a settle inside the published chrome zone must make no offer")
            .That(s.Engine.CurrentOffer).IsNull();

        zones.Clear();
        s.Context.KeyboardVM.IsVisible = true;   // any listed trigger re-publishes
        Pump();

        s.InteractionManager.OnFixationSettled(new PhysicalPoint(500, 500));
        Check.WithCustomMessage("once the zone is gone the settle must place")
            .That(s.Engine.CurrentOffer).IsNotNull();
    }

    [StaFact]
    public void EveryListedTrigger_SchedulesAPublication()
    {
        // The plan's trigger list: the initial publish, keyboard show/hide, the prediction bar's
        // show/hide (emoji mode), action-menu show/hide, modes-menu show/hide, a toolbar settings
        // change, a display change and the keyboard edge flip. Mutation: drop any one subscription →
        // that step's count does not move.
        int collected = 0;
        var s = Build(_ => { collected++; return Array.Empty<PhysicalRect>(); });
        using var _ = s.Context.GazeEngine;

        Check.That(collected).IsEqualTo(1);   // OnInitialize

        s.Context.KeyboardVM.IsVisible = true;
        Pump();
        Check.That(collected).IsEqualTo(2);

        s.Context.KeyboardVM.IsEmojiMode = true;   // the prediction bar collapses
        Pump();
        Check.That(collected).IsEqualTo(3);

        s.Context.ActionMenuVM.IsVisible = true;
        Pump();
        Check.That(collected).IsEqualTo(4);

        s.Context.ModesMenuVM.IsVisible = true;
        Pump();
        Check.That(collected).IsEqualTo(5);

        s.Context.Settings.Toolbar.Size += 1;
        Pump();
        Check.That(collected).IsEqualTo(6);

        s.Messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(3840, 2400, 2, 2)));
        Pump();
        Check.That(collected).IsEqualTo(7);

        s.Messenger.Send(new KeyboardEdgeToggledMessage());
        Pump();
        Check.That(collected).IsEqualTo(8);
    }

    [StaFact]
    public void ExternalDwellActive_RequestsTheOfferClear()
    {
        // A cooperating app's dwell takes the interaction over: the request must go through the
        // gaze-thread pattern (offer still live until a sample applies it), not clear directly.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        s.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        s.InteractionManager.OnFixationSettled(new PhysicalPoint(500, 500));
        Check.That(s.Engine.CurrentOffer).IsNotNull();

        s.Messenger.Send(new ExternalDwellActiveMessage(true));
        Pump();
        Check.WithCustomMessage("the teardown must be a request, not a dispatcher-thread mutation")
            .That(s.Engine.CurrentOffer).IsNotNull();

        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.That(s.Engine.CurrentOffer).IsNull();
    }

    [StaFact]
    public void DisplayChange_ReconfiguresTheEngineGeometry()
    {
        // The screen bounds and scale are App knowledge: after a display change the coordinator hands
        // the new geometry to Core, whose next sample rebuilds the engine. Mutation: drop the
        // ConfigureActivationOffers call on the display change → the same engine survives and this
        // goes red.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        // The startup configure is pending; the first sample applies it and attaches an engine.
        s.Provider.Raise(new GazePoint(500, 500, 33_000, true));
        var before = s.InteractionManager.ActivationOffers;
        Check.That(before).IsNotNull();

        s.Messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1920, 1200, 1.0, 1.0)));
        Pump();
        s.Provider.Raise(new GazePoint(500, 500, 66_000, true));

        Check.WithCustomMessage("the display change must rebuild the offer engine")
            .That(ReferenceEquals(s.InteractionManager.ActivationOffers, before)).IsFalse();
        Check.That(s.InteractionManager.ActivationOffers).IsNotNull();
    }
}
