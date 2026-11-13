namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The keyboard's POSITION-only moves: the toolbar-driven Yield and the gaze-driven UnYield move
/// the window while nothing else the offer coordinator or the gaze-consumer inventory listens to
/// fires (no toolbar-visibility event — the toolbar is already gone — and no VM property flip).
///
/// <para>
/// Both consumers already cache or re-check a rect, so each needs the window's own
/// <c>LocationChanged</c> as a trigger: the coordinator republishes its chrome zones through the
/// existing coalesced/deferred <c>SchedulePublish</c>, the inventory raises <c>Changed</c> through
/// its one-dispatcher-hop re-check. These tests drive the real <see cref="KeyboardWindow"/>
/// placements (<c>PlaceForShow</c> / <c>Place(…, KeyboardMove.Yield|UnYield)</c> — the exact rules
/// production's <c>RepositionOnScreen</c> and the gaze-off gate call), never a Show() on screen:
/// the window is made fully transparent and non-activating first (F6).
/// </para>
///
/// <para>
/// The mutants these pin (each must be RED): the coordinator's LocationChanged hook removed → the
/// UnYield test sees no republish; the inventory's hook removed → no <c>Changed</c> on a
/// position-only move; either <c>Dispose</c> unhook removed → the after-dispose move still
/// publishes/raises.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardMoveChromeZonesTests
{
    private const double ToolbarExtentDip = 108;

    [StaFact]
    public void UnYield_RepublishesTheKeyboardZone_AtItsUnYieldedRect()
    {
        // Owner geometry: keyboard docked Top, toolbar docked Top. Yield moves the keyboard down by
        // the toolbar extent; the toolbar hides (nothing moves — C1); the gaze-off gate's UnYield
        // moves it back. That last move is the bug this ticket fixes: it raises no trigger the
        // coordinator listens to, so the published zone stayed at the yielded rect (the round
        // button on the keyboard's strip, none in the vacated strip). Mutation: drop
        // HookKeyboardLocationEvents (or its LocationChanged subscription) → no republish after
        // the UnYield, red.
        var keyboard = NewShownKeyboard();
        try
        {
            keyboard.SetScreenEdge("top");
            keyboard.PlaceForShow(
                SystemParameters.WorkArea, keyboard.ActualWidth, keyboard.ActualHeight);

            using var rig = new CoordinatorRig(keyboard);
            Check.That(rig.Publishes).IsEqualTo(1);   // OnInitialize
            Check.That(rig.Last.HasValue).IsTrue();
            var unYielded = rig.Last!.Value;

            keyboard.SetToolbarOccupancy(true, "top", ToolbarExtentDip);
            Pump();
            Check.That(rig.Last.HasValue).IsTrue();
            var yielded = rig.Last!.Value;
            Check.WithCustomMessage("the Yield must have moved the keyboard down from the Top edge")
                .That(yielded.Y).IsStrictlyGreaterThan(unYielded.Y);
            var publishesAfterYield = rig.Publishes;

            keyboard.SetToolbarOccupancy(false, "none", 0);
            Pump();
            Check.WithCustomMessage("a hiding toolbar must not move the keyboard (C1)")
                .That(rig.Publishes).IsEqualTo(publishesAfterYield);

            keyboard.Place(
                SystemParameters.WorkArea, keyboard.ActualWidth, keyboard.ActualHeight,
                KeyboardMove.UnYield);
            Pump();

            Check.WithCustomMessage(
                    "the UnYield must republish the chrome zones — without the window's "
                    + "LocationChanged hook the yielded rect survives")
                .That(rig.Publishes).IsEqualTo(publishesAfterYield + 1);
            Check.That(rig.Last.HasValue).IsTrue();
            CheckSameRect(unYielded, rig.Last!.Value, "the republished zone after the UnYield");
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void Yield_RepublishesTheKeyboardZone_AtTheYieldedRect()
    {
        // The same pin for a Yield (a regression guard: production also reaches this through
        // ToolbarVisibilityChanged, so it was believed covered). The coordinates follow the window,
        // and the size is untouched — a position-only move.
        var keyboard = NewShownKeyboard();
        try
        {
            keyboard.SetScreenEdge("top");
            keyboard.PlaceForShow(
                SystemParameters.WorkArea, keyboard.ActualWidth, keyboard.ActualHeight);

            using var rig = new CoordinatorRig(keyboard);
            Check.That(rig.Last.HasValue).IsTrue();
            var unYielded = rig.Last!.Value;
            var publishesBefore = rig.Publishes;

            keyboard.SetToolbarOccupancy(true, "top", ToolbarExtentDip);
            Pump();

            Check.WithCustomMessage("the Yield must republish the zone")
                .That(rig.Publishes).IsEqualTo(publishesBefore + 1);
            Check.That(rig.Last.HasValue).IsTrue();
            var yielded = rig.Last!.Value;
            Check.WithCustomMessage("the published zone must sit at the yielded rect")
                .That(yielded.Y).IsStrictlyGreaterThan(unYielded.Y);
            Check.WithCustomMessage("a Yield is a position-only move for the same size")
                .That(yielded.Height).IsCloseTo(unYielded.Height, 1e-6);
            Check.That(yielded.Width).IsCloseTo(unYielded.Width, 1e-6);
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void ABurstOfPositionAndLayoutTriggers_CoalescesToOnePublish()
    {
        // The existing SchedulePublish contract must survive the new trigger: a yield that moves
        // the window AND changes its layout (the prediction bar's collapse is the VM flip) still
        // costs one collect per dispatcher turn, not one per event.
        var keyboard = NewShownKeyboard();
        try
        {
            keyboard.SetScreenEdge("top");
            keyboard.PlaceForShow(
                SystemParameters.WorkArea, keyboard.ActualWidth, keyboard.ActualHeight);

            using var rig = new CoordinatorRig(keyboard);
            var before = rig.Publishes;

            keyboard.Top += 40;                 // LocationChanged
            keyboard.Top += 40;                 // LocationChanged
            rig.KeyboardVm.IsEmojiMode = true;  // a layout trigger (the VM pair)
            keyboard.Top += 40;                 // and another move, same turn

            Pump();

            Check.WithCustomMessage("a burst of triggers must coalesce to one publish")
                .That(rig.Publishes).IsEqualTo(before + 1);
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void AfterDispose_AKeyboardMoveNoLongerPublishes()
    {
        // The unhook in OnDispose. The container's dispose path runs only at shutdown in
        // production, but a leaked subscription would keep publishing after the coordinator is
        // gone (and, in a test, outlive its context). Mutation: drop the OnDispose unhook → the
        // move after dispose still reaches the collector, red.
        var keyboard = NewShownKeyboard();
        try
        {
            keyboard.SetScreenEdge("top");
            keyboard.PlaceForShow(
                SystemParameters.WorkArea, keyboard.ActualWidth, keyboard.ActualHeight);

            var rig = new CoordinatorRig(keyboard);
            var publishesWhileAlive = rig.Publishes;
            rig.Dispose();

            keyboard.Top += 40;
            Pump();

            Check.WithCustomMessage("a disposed coordinator must not react to a keyboard move")
                .That(rig.Publishes).IsEqualTo(publishesWhileAlive);
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void PositionOnlyMove_RaisesChanged_AndLiveReportsTheNewRect()
    {
        // The inventory's half: a position-only move (no size change, no VM flip) must raise the
        // re-check signal and Live() must then report the moved rect. This is the panel staying
        // over a keyboard that moved back — the R4-H1 shape. Mutation: drop the inventory's
        // LocationChanged hook → no Changed, red.
        var keyboard = NewShownKeyboard();
        try
        {
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var keyboardVm = new KeyboardViewModel(settings, messenger) { IsVisible = true };
            using var inventory = BuildInventory(settings, messenger, keyboardVm, keyboard);

            var before = inventory.Live();
            Check.That(before).HasSize(1);
            var beforeRect = before[0];
            double scale = beforeRect.Height / keyboard.ActualHeight;

            var fired = 0;
            inventory.Changed += () => fired++;

            keyboard.Top += 100;
            Pump();

            Check.WithCustomMessage("a position-only move must raise the inventory's re-check")
                .That(fired).IsStrictlyGreaterThan(0);

            var after = inventory.Live();
            Check.That(after).HasSize(1);
            Check.WithCustomMessage("the move must not change the keyboard's size")
                .That(after[0].Width).IsCloseTo(beforeRect.Width, 1e-6);
            Check.That(after[0].Height).IsCloseTo(beforeRect.Height, 1e-6);
            Check.WithCustomMessage("Live() must report the moved rect, not the cached one")
                .That(after[0].Y - beforeRect.Y).IsCloseTo(100 * scale, 1.5);
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void AfterDispose_APositionOnlyMoveNoLongerRaisesChanged()
    {
        // The inventory's Dispose half: the window is app-lifetime, the inventory is not, so the
        // subscription must not outlive it. Mutation: drop the location unhook in Dispose → red.
        var keyboard = NewShownKeyboard();
        try
        {
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var keyboardVm = new KeyboardViewModel(settings, messenger) { IsVisible = true };
            var inventory = BuildInventory(settings, messenger, keyboardVm, keyboard);

            var fired = 0;
            inventory.Changed += () => fired++;
            Check.That(inventory.Live()).HasSize(1);   // hooks the window
            inventory.Dispose();

            keyboard.Top += 40;
            Pump();

            Check.WithCustomMessage("a disposed inventory must not react to a keyboard move")
                .That(fired).IsEqualTo(0);
        }
        finally
        {
            keyboard.Close();
        }
    }

    /// <summary>
    /// A real <see cref="KeyboardWindow"/> SHOWN invisibly (F6: opacity 0, no activation — layout,
    /// geometry and the window's events run identically; nothing is painted). The caller closes it.
    /// </summary>
    private static KeyboardWindow NewShownKeyboard()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var keyboard = InvisibleTestWindow.Prepare(new KeyboardWindow());
        keyboard.Show();
        Pump();
        return keyboard;
    }

    private static GazeConsumerInventory BuildInventory(
        EyeCtrlSettings settings, IMessenger messenger, KeyboardViewModel keyboardVm,
        KeyboardWindow keyboard)
        => new(
            settings,
            () => new KeyboardOnlyOverlays(keyboard),
            keyboardVm,
            new ActionMenuViewModel(settings, messenger),
            new ModesMenuViewModel(settings, messenger),
            new EdgeButtonBarViewModel(settings, messenger),
            new EdgeButtonBarViewModel(settings, messenger),
            () => Array.Empty<PhysicalRect>(),
            () => Array.Empty<PhysicalRect>(),
            () => Array.Empty<PhysicalRect>(),
            () => Array.Empty<PhysicalRect>(),
            new ToolbarLifecycleBridge(),
            messenger);

    // Below the deferred publish (Loaded) and the inventory's Changed raise (Background): a marker
    // at ContextIdle runs after both have been drained.
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void CheckSameRect(PhysicalRect expected, PhysicalRect actual, string what)
    {
        Check.WithCustomMessage($"{what}: expected {expected}, got {actual}")
            .That(Math.Abs(actual.X - expected.X) <= 1.0
                && Math.Abs(actual.Y - expected.Y) <= 1.0
                && Math.Abs(actual.Width - expected.Width) <= 1.0
                && Math.Abs(actual.Height - expected.Height) <= 1.0)
            .IsTrue();
    }

    /// <summary>
    /// The offer coordinator with a REAL keyboard window behind the overlay-manager seam: only
    /// <c>Keyboard</c> exists (the collector is injected and reads the window's own chrome bounds —
    /// the production collector minus the toolbar/menus, which this rig does not show), everything
    /// else throws so an accidental reach fails loudly.
    /// </summary>
    private sealed class CoordinatorRig : IDisposable
    {
        private readonly GazeEngine _engine;
        private readonly KeyboardWindow _keyboard;

        public CoordinatorRig(KeyboardWindow keyboard)
        {
            _keyboard = keyboard;
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            _engine = new GazeEngine(
                new TestFakes.FakeGazeProvider(), new TestFakes.FakeInputSimulator(), settings);
            KeyboardVm = new KeyboardViewModel(settings, messenger) { IsVisible = true };
            Context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                GazeEngine = _engine,
                InteractionManager = _engine.InteractionManager,
                Messenger = messenger,
                Dispatcher = Dispatcher.CurrentDispatcher,
                Settings = settings,
                KeyboardVM = KeyboardVm,
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                ModesMenuVM = new ModesMenuViewModel(settings, messenger),
                TaskSwitcherVM = new TaskSwitcherViewModel(
                    new TestFakes.FakeWindowActionManager(), settings, messenger),
                QuickActionsGridVM = new QuickActionsGridViewModel(
                    new NoOpQuickActionsStore(), settings, messenger),
                OverlayManager = new KeyboardOnlyOverlays(keyboard),
            };
            Coordinator = new ActivationOfferCoordinator(
                new ToolbarLifecycleCoordinator(new ModalInteractionState()),
                new ModalInteractionState(),
                Collect);
            Coordinator.Initialize(Context);
            // The initialize publish is deferred one layout pass (SchedulePublish); drain it so the
            // tests start from a known published state.
            Pump();
        }

        public KeyboardViewModel KeyboardVm { get; }

        public EventCoordinationContext Context { get; }

        public ActivationOfferCoordinator Coordinator { get; }

        /// <summary>How many times the coordinator collected the zones (one per publish).</summary>
        public int Publishes { get; private set; }

        /// <summary>The keyboard rect the last publish collected; null when it could not be sourced.</summary>
        public PhysicalRect? Last { get; private set; }

        private PhysicalRect[] Collect(EventCoordinationContext _)
        {
            Publishes++;
            if (!_keyboard.TryGetChromeBounds(out var rect))
            {
                Last = null;
                return Array.Empty<PhysicalRect>();
            }
            Last = rect;
            return new[] { rect };
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            _engine.Dispose();
        }
    }

    private sealed class NoOpQuickActionsStore : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }

    /// <summary>
    /// Only the keyboard seam is real (the manager's <c>Keyboard</c> is the concrete app-lifetime
    /// window and cannot be faked); every other member throws — the <c>PartialOverlayManager</c>
    /// discipline, so a test that over-reaches fails loudly.
    /// </summary>
    private sealed class KeyboardOnlyOverlays : IOverlayManager
    {
        private readonly KeyboardWindow _keyboard;

        public KeyboardOnlyOverlays(KeyboardWindow keyboard) => _keyboard = keyboard;

        public KeyboardWindow Keyboard => _keyboard;
        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => throw new NotImplementedException();
        public IGlassBackdropOverlay GlassBackdrop => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar => throw new NotImplementedException();
        public void Initialize(
            OverlayViewModels viewModels, EyeCtrlSettings settings,
            Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();
        public void CloseAll() => throw new NotImplementedException();
    }
}
