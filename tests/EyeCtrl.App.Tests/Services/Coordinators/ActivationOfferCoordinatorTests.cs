namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8's offer coordinator: the geometry hand-off, the chrome-zone publication on every trigger
/// the plan lists, the modal-surface suspension, and the external-dwell teardown. The zone collector
/// is injected, so these exercise the coordinator's subscriptions and the push into
/// <see cref="InteractionManager"/> without standing up the keyboard/menu windows; the pure
/// composition is pinned in <see cref="ActivationChromeZonesTests"/> and the production collector is
/// a thin read on top of it.
/// </summary>
[Collection("WpfApplication")]
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
        TestFakes.FakeGazeProvider Provider,
        ModalInteractionState Modal);

    private static Setup Build(Func<EventCoordinationContext, PhysicalRect[]>? collect = null)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var provider = new TestFakes.FakeGazeProvider();
        var gazeEngine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        var im = gazeEngine.InteractionManager;
        var engine = new ActivationOfferEngine(Screen, scale: 2.0, ringSizeDip: 250, dwellMs: 700, fixationRadiusPx: 90);
        im.AttachActivationOffers(engine);

        var modal = new ModalInteractionState();
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
            TaskSwitcherVM = new TaskSwitcherViewModel(new TestFakes.FakeWindowActionManager(), settings, messenger),
            QuickActionsGridVM = new QuickActionsGridViewModel(new NoOpQuickActionsStore(), settings, messenger),
        };

        var coordinator = new ActivationOfferCoordinator(
            new ToolbarLifecycleCoordinator(modal),
            modal,
            collect ?? (_ => Array.Empty<PhysicalRect>()));
        coordinator.Initialize(context);
        Pump();
        return new Setup(coordinator, context, im, engine, messenger, Dispatcher.CurrentDispatcher, provider, modal);
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

    [StaFact]
    public void ConfigureEngine_UsesPhysicalScreenBounds_AtTheLiveScale()
    {
        // Review A A9: dropping the ×ScaleX survives every other test, and at ScaleX 2.0 the clamp
        // would pin every B inside the top-left quarter. Mutation: use DIP bounds, no scale.
        double previousX = DpiHelper.ScaleX;
        double previousY = DpiHelper.ScaleY;
        try
        {
            DpiHelper.Initialize(2.0, 2.0);
            var s = Build();
            using var _ = s.Context.GazeEngine;

            s.Provider.Raise(new GazePoint(500, 500, 33_000, true));
            var engine = s.InteractionManager.ActivationOffers;
            Check.That(engine).IsNotNull();
            Check.That(engine!.ScreenBoundsPx.Width)
                .IsCloseTo(System.Windows.SystemParameters.PrimaryScreenWidth * 2.0, 1e-9);
            Check.That(engine.ScreenBoundsPx.Height)
                .IsCloseTo(System.Windows.SystemParameters.PrimaryScreenHeight * 2.0, 1e-9);
            Check.That(engine.Scale).IsEqualTo(2.0);
        }
        finally
        {
            DpiHelper.Initialize(previousX, previousY);
        }
    }

    [StaFact]
    public void EachModalSurface_SuspendsOffers_WhileOpen()
    {
        // The user's decision (2026-10-02): while Settings, the task switcher, the quick-actions grid
        // or the eye-care banner is open, no offer is made and a live unfired one is cleared.
        // Mutation (per surface): drop its subscription → the offer survives its opening, red.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        // Settings: the writer raises the event (SettingsUiCoordinator's contract).
        var offer = Settle(s, 500, 500);
        s.Modal.SettingsOpen = true;
        s.Modal.SettingsOpenChanged?.Invoke(true);
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.WithCustomMessage("opening Settings must clear the live offer")
            .That(s.Engine.CurrentOffer).IsNull();
        s.Modal.SettingsOpen = false;
        s.Modal.SettingsOpenChanged?.Invoke(false);
        Check.That(Settle(s, 600, 600)).IsNotNull();

        // Task switcher.
        s.InteractionManager.RequestActivationOfferClear();
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        s.Context.TaskSwitcherVM.IsVisible = true;
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        var whileTaskSwitcher = Settle(s, 700, 700);
        Check.WithCustomMessage("no offer may be made while the task switcher is open")
            .That(whileTaskSwitcher).IsNull();
        s.Context.TaskSwitcherVM.IsVisible = false;

        // Quick-actions grid.
        s.Context.QuickActionsGridVM.IsVisible = true;
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.That(Settle(s, 800, 800)).IsNull();
        s.Context.QuickActionsGridVM.IsVisible = false;

        // Eye-care banner.
        s.Messenger.Send(new EyeCareBannerActiveMessage(true));
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.That(Settle(s, 900, 900)).IsNull();
        s.Messenger.Send(new EyeCareBannerActiveMessage(false));
        s.InteractionManager.ApplyPendingActivationOfferRequests();

        Check.That(Settle(s, 1000, 1000)).IsNotNull();
    }

    [StaFact]
    public void ProbeOpen_AloneSuspendsOffers_AndResumesOnClose()
    {
        // W5(c) (build plan): the probe term on the offer suspension, with NO other modal surface
        // open. Opening the probe alone must clear a live offer and refuse new ones — the probe's
        // window is opaque, so an offer halo underneath it is an invisible, uncountable target.
        // Mutations: drop the `if (_modal.ProbeOpen)` term from RecomputeSuspension, or drop the
        // ProbeOpenChanged subscription → the settle under the probe offers → red.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        Check.That(Settle(s, 500, 500)).IsNotNull();

        s.Modal.ProbeOpen = true;   // raises ProbeOpenChanged → RecomputeSuspension
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.WithCustomMessage("opening the probe must clear the live offer")
            .That(s.Engine.CurrentOffer).IsNull();
        Check.WithCustomMessage("no offer may be made while the probe is open")
            .That(Settle(s, 600, 600)).IsNull();

        s.Modal.ProbeOpen = false;  // raises again → offers resume
        Check.WithCustomMessage("once the probe closes, settles must offer again")
            .That(Settle(s, 700, 700)).IsNotNull();
    }

    /// <summary>Ends any live fixation, settles at a fresh point through the public API, and returns
    /// the offer (or null when none was made).</summary>
    private static ActivationOffer? Settle(Setup s, double x, double y)
    {
        s.InteractionManager.OnFixationEnded();
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        var point = new PhysicalPoint(x, y);
        s.InteractionManager.OnFixationStarted(point);
        s.InteractionManager.OnFixationSettled(point);
        return s.InteractionManager.ActivationOffers!.CurrentOffer;
    }

    private sealed class NoOpQuickActionsStore : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }
}
