namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.IO;
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
using Serilog;
using Serilog.Core;
using Serilog.Events;
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
    public void PreviewPanelVisible_SuspendsOffers_AndLogsTheReason()
    {
        // Task 12 / D16: the preview panel is the FIFTH suspension input. While it is up no offer
        // is made and a live one is cleared, and — like the other four inputs — the change is
        // logged with the input's name, so a live session's gap in Offer-shown lines is
        // explainable (ticket 8 fixup 02). Mutation: drop the PreviewPanelActiveMessage
        // registration → the settle offers and no "adaptation preview" reason is logged.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        var sink = new CapturingSink();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(sink)
                .CreateLogger();

            Check.That(Settle(s, 500, 500)).IsNotNull();

            s.Messenger.Send(new PreviewPanelActiveMessage(true));
            s.InteractionManager.ApplyPendingActivationOfferRequests();
            Check.WithCustomMessage("a visible preview panel must suspend offers")
                .That(Settle(s, 600, 600)).IsNull();

            var suspension = sink.Events
                .Where(e => e.MessageTemplate.Text.Contains(
                    "Activation offers suspended", StringComparison.Ordinal))
                .ToList();
            Check.WithCustomMessage(
                "the preview's suspension must be logged as Suspended=true with the reason "
                + "'adaptation preview' — log events captured: " + Describe(sink.Events))
                .That(suspension.Any(e =>
                    BoolProperty(e, "Suspended") == true
                    && StringProperty(e, "Reason") == "adaptation preview"))
                .IsTrue();
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [StaFact]
    public void PreviewPanelHides_WhileSettingsIsOpen_OffersStaySuspended()
    {
        // The combined-flag test: the preview's hide recombines ALL five inputs, and Settings
        // still being open keeps the flag set. A second writer publishing its own true/false
        // (the service writing the flag directly) would resume offers here — behind the user's
        // open Settings window.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        s.Modal.SettingsOpen = true;
        s.Modal.SettingsOpenChanged?.Invoke(true);
        s.Messenger.Send(new PreviewPanelActiveMessage(true));
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.WithCustomMessage("Settings + preview open must suspend offers")
            .That(Settle(s, 700, 700)).IsNull();

        s.Messenger.Send(new PreviewPanelActiveMessage(false));
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.WithCustomMessage(
            "the preview's hide must not resume offers while Settings is still open — the "
            + "single writer recomputes from all five inputs")
            .That(Settle(s, 800, 800)).IsNull();
    }

    [StaFact]
    public void PreviewPanelHides_WithNothingElseHolding_OffersResume()
    {
        // P16: the hide edge (the service's user-leave, policy hide and R2-2 teardown all
        // publish false) must release the suspension when no other input holds — a stuck
        // suspension would keep offers off until restart.
        var s = Build();
        using var _ = s.Context.GazeEngine;

        s.Messenger.Send(new PreviewPanelActiveMessage(true));
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.That(Settle(s, 700, 700)).IsNull();

        s.Messenger.Send(new PreviewPanelActiveMessage(false));
        s.InteractionManager.ApplyPendingActivationOfferRequests();
        Check.WithCustomMessage("offers must resume once the preview hides and nothing else holds (P16)")
            .That(Settle(s, 800, 800)).IsNotNull();
    }

    // The single-writer invariant (D16): the offers-suspension flag has ONE writer — this
    // coordinator, whose RecomputeSuspension OR-combines the five inputs. The allowlist pins the
    // exact allowed set of source files naming SetActivationOffersSuspended( (the
    // DwellSurfaceCensusTests pattern): the writer's call site and Core's definition. The
    // preview service — like the banner and the three VM-driven surfaces — goes through a
    // message; a second writer fails here by name.
    private static readonly string[] SuspensionFlagFiles =
    [
        "EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs", // the single writer
        "EyeCtrl.Core/Processing/InteractionManager.cs",                   // the definition
    ];

    [Fact]
    public void TheOfferCoordinator_IsTheOnlyWriterOfTheSuspensionFlag()
    {
        var found = SuspensionFlagSourceFiles();
        var unexpected = found
            .Where(f => !SuspensionFlagFiles.Contains(f, StringComparer.Ordinal)).ToList();
        Check.WithCustomMessage(
            "These files write (or name) SetActivationOffersSuspended but are not in the "
            + "allowlist — the flag has one writer, ActivationOfferCoordinator; publish a "
            + "PreviewPanelActiveMessage (or the surface's own event) and let RecomputeSuspension "
            + "combine it: " + string.Join(", ", unexpected))
            .That(unexpected).IsEmpty();

        foreach (var path in SuspensionFlagFiles)
        {
            var full = Path.Combine(SourceScanner.RepoRoot().FullName, "src",
                path.Replace('/', Path.DirectorySeparatorChar));
            Check.WithCustomMessage($"Allowlist entry '{path}' no longer exists on disk.")
                .That(File.Exists(full)).IsTrue();
            Check.WithCustomMessage(
                $"Allowlist entry '{path}' no longer names SetActivationOffersSuspended — "
                + "update the census to match the code.")
                .That(File.ReadAllText(full).Contains("SetActivationOffersSuspended(")).IsTrue();
        }
    }

    private static List<string> SuspensionFlagSourceFiles()
    {
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("SetActivationOffersSuspended("))
            .Select(f => f[(src.Length + 1)..].Replace('\\', '/'))
            .ToList();
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

    /// <summary>Captures the events the coordinator logs while a test drives it (the
    /// KeyboardCategoryGridExclusionTests pattern). Assertions read the structured properties,
    /// never a rendered message, so an honest rewording does not red them.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static string? StringProperty(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            && value is ScalarValue { Value: string s } ? s : null;

    private static bool? BoolProperty(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            && value is ScalarValue { Value: bool b } ? b : null;

    private static string Describe(IEnumerable<LogEvent> events) =>
        string.Join("; ", events.Select(e => e.RenderMessage()));

    private sealed class NoOpQuickActionsStore : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }
}
