namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The D17 gaze-consumer inventory (Task 11): which surfaces a preview panel rect must not cover.
/// The inventory is the concrete <see cref="IOverlayCollision"/> the preview service gates on, and
/// this class pins (a) the mapping — each named source contributes its rect only while that source
/// is up, reusing the ticket-8 derived rects for the two menus; (b) the edge-button bar's new
/// derived rect (the union of the dwell hit areas, the same over-cover rule the menus use);
/// (c) the <c>Changed</c> trigger set — a SUPERSET of what
/// <c>ActivationOfferCoordinator</c>.SchedulePublish listens to (the coordinator's
/// ExternalDwellActiveMessage handler clears offers rather than publishing zones), plus the bars'
/// own button-set changes, the note-targets message (review F1), external dwell's structural
/// message (fix-wave F5) and the restart-notice card's show/hide (T15 merge);
/// (d) the strict overlap rule (<c>touching is clear</c>, Task 1) at the IsClear seam.
/// </summary>
/// <remarks>
/// In the <c>WpfApplication</c> collection only because the keyboard-source tests construct a real
/// <see cref="KeyboardWindow"/> (the manager's Keyboard property is the concrete window — it cannot
/// be faked; the same reason <c>ActivationChromeZonesTests</c> pins the keyboard rule through
/// <c>Compose</c>). Everything here runs headless apart from needing the token dictionary for that
/// construction. No test writes <c>DpiHelper</c>, so all value assertions are scale-agnostic or
/// take the scale as a literal argument.
/// </remarks>
[Collection("WpfApplication")]
public class GazeConsumerInventoryTests
{
    private static EdgeButtonDefinition Def(
        string id, double verticalOffset, double radiusLogical = 30) =>
        new(id, Symbol: "◆", DwellMs: 800, Activate: static _ => { },
            VerticalOffset: verticalOffset, RadiusLogical: radiusLogical);

    private sealed class Kit
    {
        public readonly EyeCtrlSettings Settings = new();
        public readonly WeakReferenceMessenger Messenger = new();
        public readonly KeyboardViewModel KeyboardVm;
        public readonly ViewModels.ActionMenuViewModel ActionMenuVm;
        public readonly ModesMenuViewModel ModesMenuVm;
        public readonly EdgeButtonBarViewModel EdgeBarVm;
        public readonly EdgeButtonBarViewModel ModBarVm;
        public readonly ToolbarLifecycleBridge Bridge = new();
        public readonly Func<IReadOnlyList<PhysicalRect>> ExternalRects = () => Array.Empty<PhysicalRect>();
        public Func<IReadOnlyList<PhysicalRect>> NoteRects { get; set; } = () => Array.Empty<PhysicalRect>();
        public Func<IReadOnlyList<PhysicalRect>> RestartRects { get; set; } = () => Array.Empty<PhysicalRect>();
        public Func<IOverlayManager> Overlays = () => throw new NotImplementedException("no keyboard supplied");
        public GazeConsumerInventory Inventory = null!;

        public Kit()
        {
            KeyboardVm = new KeyboardViewModel(Settings, Messenger);
            ActionMenuVm = new ViewModels.ActionMenuViewModel(Settings, Messenger);
            ModesMenuVm = new ModesMenuViewModel(Settings, Messenger);
            EdgeBarVm = new EdgeButtonBarViewModel(Settings, Messenger);
            ModBarVm = new EdgeButtonBarViewModel(Settings, Messenger);
        }

        /// <summary>Built after the test has tweaked the VMs/overlays seam it needs.</summary>
        public GazeConsumerInventory Build(Func<IReadOnlyList<PhysicalRect>>? external = null)
        {
            Inventory = new GazeConsumerInventory(
                Settings,
                Overlays,
                KeyboardVm,
                ActionMenuVm,
                ModesMenuVm,
                EdgeBarVm,
                ModBarVm,
                external ?? ExternalRects,
                NoteRects,
                RestartRects,
                Bridge,
                Messenger);
            return Inventory;
        }
    }

    // ── the pure edge-bar rect: the union of the dwell hit areas ──

    [Fact]
    public void EdgeButtonBarBounds_TwoButtons_IsTheUnionOfTheHitSquares()
    {
        // Anchor (1000, 500); buttons at -40/+40 with 30 DIP radii; literal scale 1.5 so every
        // number is hand-derived: physical radius 45, centers (1000, 460) and (1000, 540).
        var centers = new Dictionary<string, PhysicalPoint>
        {
            ["a"] = new(1000, 460),
            ["b"] = new(1000, 540),
        };
        var buttons = new[] { ("a", 30.0), ("b", 30.0) };

        var rect = GazeConsumerInventory.EdgeButtonBarBounds(centers, buttons, scale: 1.5);

        Check.That(rect).IsNotNull();
        Check.That(rect!.Value.X).IsEqualTo(1000 - 45);
        Check.That(rect.Value.Y).IsEqualTo(460 - 45);
        Check.That(rect.Value.Width).IsEqualTo(90);
        // (540 + 45) - (460 - 45): the union spans both squares, not one.
        Check.That(rect.Value.Height).IsEqualTo(170);
    }

    [Fact]
    public void EdgeButtonBarBounds_NoButtons_IsNull()
    {
        // A bar with no buttons has no hit area — null, never a zero rect at the origin (the
        // same under-cover-versus-phantom-rect rule Compose pins for the keyboard).
        Check.That(GazeConsumerInventory.EdgeButtonBarBounds(
            new Dictionary<string, PhysicalPoint>(), Array.Empty<(string, double)>(), 1.0)).IsNull();
    }

    // ── Live(): each named source, gated on its own visibility ──

    [Fact]
    public void Live_NothingUp_IsEmpty()
    {
        var kit = new Kit();
        using var inventory = kit.Build();

        Check.That(inventory.Live()).IsEmpty();
    }

    [StaFact]
    public void Live_KeyboardVisibleButNotLaidOut_ContributesNoRect()
    {
        // The manager's Keyboard property is the concrete window, so this is a real one — never
        // shown, so TryGetChromeBounds is false. The rule (the ticket-8 keyboard rule): visible
        // alone is NOT enough; an unsourced window contributes nothing rather than a phantom rect.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var keyboard = new KeyboardWindow();
        try
        {
            keyboard.SetViewModel(new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()));
            var kit = new Kit();
            kit.Overlays = () => new KeyboardOnlyOverlays(keyboard);

            using var inventory = kit.Build();
            kit.KeyboardVm.IsVisible = true;

            Check.That(inventory.Live()).IsEmpty();
        }
        finally
        {
            keyboard.Close();
        }
    }

    [StaFact]
    public void Live_KeyboardLaidOut_ContributesItsChromeRect()
    {
        // The positive half the headless test above cannot reach: once the window has actually
        // laid out, its chrome rect (grid + prediction bar + category bar — TryGetChromeBounds) is
        // exactly what Live() reports, one rect, nothing else.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var keyboard = new KeyboardWindow();
        try
        {
            keyboard.SetViewModel(new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()));
            var kit = new Kit();
            kit.Overlays = () => new KeyboardOnlyOverlays(keyboard);

            using var inventory = kit.Build();
            keyboard.Show();
            Pump();
            kit.KeyboardVm.IsVisible = true;

            Check.WithCustomMessage(
                    "The real keyboard window did not lay out in this session, so its bounds " +
                    "cannot be sourced; failing loudly rather than vacuously.")
                .That(keyboard.TryGetChromeBounds(out _)).IsTrue();

            Check.That(keyboard.TryGetChromeBounds(out var chrome)).IsTrue();
            Check.That(inventory.Live()).ContainsExactly(chrome);
        }
        finally
        {
            keyboard.Hide();
            keyboard.Close();
        }
    }

    [Fact]
    public void Live_EdgeBarUp_ContributesOneRectSpanningTheButtons()
    {
        // Scale-agnostic (no DpiHelper reads): with the bar up, exactly one rect, and it contains
        // both button centers with room to spare. The exact union math is pinned above at a
        // literal scale.
        var kit = new Kit();
        using var inventory = kit.Build();
        kit.EdgeBarVm.ShowButtons(
            new[] { Def("close", -40), Def("flip", 40) }, new PhysicalPoint(1000, 500));

        var live = inventory.Live();

        Check.That(live).HasSize(1);
        Check.That(live[0].X).IsStrictlyLessThan(1000);
        Check.That(live[0].X + live[0].Width).IsStrictlyGreaterThan(1000);
        Check.That(live[0].Y).IsStrictlyLessThan(460);
        Check.That(live[0].Y + live[0].Height).IsStrictlyGreaterThan(540);
    }

    [Fact]
    public void Live_ModBarUp_IsInventoriedToo()
    {
        // The action menu parks its modifier buttons on a SECOND EdgeButtonBarViewModel (the keyed
        // "actionMenuModBar" singleton behind its own window). A bar-shaped window with dwell
        // buttons must not escape the inventory just because it is the other instance.
        var kit = new Kit();
        using var inventory = kit.Build();
        kit.ModBarVm.ShowButtons(
            new[] { Def("shift", 0), Def("ctrl", 80) }, new PhysicalPoint(1200, 600));

        Check.That(inventory.Live()).HasSize(1);
    }

    [Fact]
    public void Live_ActionMenuUp_ContributesTheRingWindowSquare()
    {
        // Delegates to ActivationChromeZones.ActionMenuBounds (the ticket-8 derived rect — the
        // brief's "reuse, don't duplicate" rule). Asserted scale-agnostically: the rect is a
        // square centred exactly on the menu's centre.
        var kit = new Kit();
        using var inventory = kit.Build();
        kit.ActionMenuVm.IsVisible = true;
        kit.ActionMenuVm.Center = new PhysicalPoint(900, 600);
        kit.ActionMenuVm.EffectiveRingRadius = 220;

        var live = inventory.Live();

        Check.That(live).HasSize(1);
        Check.That(live[0].X + live[0].Width / 2).IsCloseTo(900, 1e-9);
        Check.That(live[0].Y + live[0].Height / 2).IsCloseTo(600, 1e-9);
        Check.That(live[0].Width).IsCloseTo(live[0].Height, 1e-9);
        Check.That(live[0].Width).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Live_ModesMenuUp_ContributesTheRowUnion()
    {
        // Delegates to ActivationChromeZones.ModesMenuBounds — pinned by exact equality with the
        // shared pure function (that call IS the reuse under test; no DpiHelper involved).
        var kit = new Kit();
        using var inventory = kit.Build();
        var rows = new[]
        {
            ("row1", new PhysicalPoint(300, 700), 60.0),
            ("row2", new PhysicalPoint(300, 820), 60.0),
        };
        kit.ModesMenuVm.SetRowScreenPositions(rows);
        kit.ModesMenuVm.IsVisible = true;

        Check.That(inventory.Live())
            .ContainsExactly(EyeCtrl.App.Services.Coordinators.ActivationChromeZones.ModesMenuBounds(rows));
    }

    [Fact]
    public void Live_ExternalDwellTargets_ArePassedThrough()
    {
        // The external-dwell source is the service's LiveTargetRects (its own tests pin the
        // read-under-lock behaviour); here the seam just has to carry the rects into the inventory.
        var kit = new Kit();
        var external = new PhysicalRect(400, 900, 120, 60);
        using var inventory = kit.Build(() => new[] { external });

        Check.That(inventory.Live()).ContainsExactly(external);
    }

    [Fact]
    public void Live_NoteTargetRects_ArePassedThrough()
    {
        // Review F1's sixth source: NoteManager.LiveTargetRects (its own tests pin the detector-
        // snapshot geometry); here the seam just has to carry the note rects into the inventory —
        // a note parked under the panel is the same R4-H1 harm as a keyboard there.
        var kit = new Kit();
        var close = new PhysicalRect(1200, 40, 60, 60);
        var edit = new PhysicalRect(1200, 110, 60, 60);
        kit.NoteRects = () => new[] { close, edit };
        using var inventory = kit.Build();

        Check.That(inventory.Live()).ContainsExactly(close, edit);
    }

    [StaFact]
    public void Live_RestartNoticeCardUp_ContributesItsCloseTargetRect_AndBlocksThePanel()
    {
        // T15 merge: the restart-notice card is a REAL gaze consumer (its own detector over the
        // close button), so it enters the inventory through the App-attached bridge exactly the
        // way the notes enter through NoteManager — the own-detector snapshot, gated on the card
        // showing. The card is laid out headlessly via its present seam (a shown overlay is the
        // user's only input surface — the RestartNoticeWindowTests pattern); ShowNotice places
        // the dwell target without an HWND, so the source is real without a presentation source.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var card = new RestartNoticeWindow(
            dwellMs: 25, messenger, keepClear: null, present: c =>
            {
                c.Measure(new System.Windows.Size(c.Width, c.Height));
                c.Arrange(new System.Windows.Rect(0, 0, c.Width, c.Height));
                c.UpdateLayout();
            });
        try
        {
            var bridge = new RestartNoticeBridge();
            var kit = new Kit();
            kit.RestartRects = bridge.LiveTargetRects;
            using var inventory = kit.Build();

            Check.WithCustomMessage("a hidden card contributes nothing")
                .That(inventory.Live()).IsEmpty();

            card.ShowNotice("Titel\nKörper", "OK", TimeSpan.FromSeconds(30));
            Check.WithCustomMessage(
                    "an unattached bridge must degrade to empty (D14), not throw")
                .That(inventory.Live()).IsEmpty();

            bridge.Attach(card);
            var live = inventory.Live();
            Check.That(live).HasSize(1);
            var target = live[0];
            Check.WithCustomMessage("the rect must be the placed close target, not a phantom")
                .That(target.Width).IsStrictlyGreaterThan(0);
            Check.That(target.Height).IsStrictlyGreaterThan(0);
            Check.That(card.LiveTargetRects()).ContainsExactly(target);

            // The card behaves like every other source at the collision seam (Task 1's rule): a
            // panel overlapping the close button is not clear; one only touching its edge is.
            Check.That(inventory.IsClear(new PhysicalRect(target.X + 1, target.Y + 1, 50, 50)))
                .IsFalse();
            Check.That(inventory.IsClear(new PhysicalRect(
                target.X + target.Width, target.Y, 50, 50))).IsTrue();

            card.HideNotice();
            Check.WithCustomMessage("the card going must take its rect out of the inventory")
                .That(inventory.Live()).IsEmpty();
        }
        finally
        {
            card.Close();
        }
    }

    [Fact]
    public void Live_AllSourcesUpTogether_ReportsEveryRect()
    {
        var kit = new Kit();
        using var inventory = kit.Build();
        kit.EdgeBarVm.ShowButtons(new[] { Def("close", -40) }, new PhysicalPoint(1500, 400));
        kit.ActionMenuVm.IsVisible = true;
        kit.ModesMenuVm.SetRowScreenPositions(new[] { ("r", new PhysicalPoint(200, 200), 40.0) });
        kit.ModesMenuVm.IsVisible = true;

        // Keyboard stays down (headless) and external stays empty: 3 rects, one per up surface.
        Check.That(inventory.Live()).HasSize(3);
    }

    // ── IsClear: the strict overlap rule at the collision seam ──

    [Fact]
    public void IsClear_OverlappingConsumer_IsNotClear_TouchingIsClear()
    {
        // Task 1's rule reaches D17 through this seam: PhysicalRect.IntersectsWith is STRICT, so a
        // panel that only touches a consumer's edge is clear; one physical pixel of overlap is not.
        var kit = new Kit();
        var consumer = new PhysicalRect(100, 100, 200, 100);
        using var inventory = kit.Build(() => new[] { consumer });

        Check.That(inventory.IsClear(new PhysicalRect(300.5, 0, 50, 500))).IsTrue();  // touching x=300...0.5 gap
        Check.That(inventory.IsClear(new PhysicalRect(299, 0, 50, 500))).IsFalse();   // overlaps [299,349)
        Check.That(inventory.IsClear(new PhysicalRect(0, 0, 50, 50))).IsTrue();       // far away
    }

    // ── Changed: the SchedulePublish trigger set + the bars' own mutations ──

    [StaFact]
    public void Changed_FiresOnEveryChromeTrigger()
    {
        // Final review F2: Changed is now raised one dispatcher hop after the trigger (Background
        // — after layout), so the count is read after a pump. The marker sits at ContextIdle,
        // BELOW the raise's priority: a raise posted from inside the pump would be skipped by a
        // same-priority marker (FIFO within a priority ends the pump first).
        var kit = new Kit();
        using var inventory = kit.Build();
        var fired = 0;
        inventory.Changed += () => fired++;

        // Keyboard VM: visibility and emoji-mode (the prediction bar's collapse), the same pair the
        // offer coordinator filters on.
        kit.KeyboardVm.IsVisible = true;
        kit.KeyboardVm.IsVisible = false;
        kit.KeyboardVm.IsEmojiMode = true;
        // The two menus.
        kit.ActionMenuVm.IsVisible = true;
        kit.ModesMenuVm.IsVisible = true;
        // Both edge bars: ShowButtons raises ButtonSetChanged AND IsVisible (two fires each).
        kit.EdgeBarVm.ShowButtons(new[] { Def("close", 0) }, new PhysicalPoint(100, 100));
        kit.ModBarVm.ShowButtons(new[] { Def("shift", 0) }, new PhysicalPoint(100, 100));
        // A toolbar geometry edit.
        kit.Settings.Toolbar.DockedEdge = "Bottom";
        // The six messages (fix-wave F5: external-dwell's STRUCTURAL message joined the set —
        // ExternalDwellActiveMessage fires only once gaze has entered a target, so it can never
        // announce a register/update/unregister; T15 merge: the restart card's show/hide moves a
        // source in and out of the inventory).
        kit.Messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1920, 1080, 1, 1)));
        kit.Messenger.Send(new KeyboardEdgeToggledMessage());
        kit.Messenger.Send(new ExternalDwellActiveMessage(true));
        kit.Messenger.Send(new ExternalDwellTargetsChangedMessage());
        kit.Messenger.Send(new NoteTargetsChangedMessage());
        kit.Messenger.Send(new RestartNoticeActiveMessage(true));
        Pump();

        Check.WithCustomMessage(
                "Every SchedulePublish-set trigger plus the bars' button-set mutations and the " +
                "note-targets and restart-notice messages must raise Changed (count: 3 keyboard — " +
                "two visibility flips + emoji-mode — + 2 menus + 4 bars (IsVisible + " +
                "ButtonSetChanged each) + 1 toolbar settings + 6 messages = 16).")
            .That(fired).IsEqualTo(16);
    }

    [StaFact]
    public void Changed_DoesNotFireOnUnrelatedKeyboardProperties()
    {
        // The keyboard VM raises PropertyChanged continuously while typing (NearestKeyId and
        // friends); a re-check per property change is a per-keypress inventory walk. The filter is
        // the offer coordinator's pair (IsVisible, IsEmojiMode) — nothing else. Pumped, so the
        // assertion covers the deferred raise too, not just the synchronous filter.
        var kit = new Kit();
        using var inventory = kit.Build();
        var fired = 0;
        inventory.Changed += () => fired++;

        kit.KeyboardVm.NearestKeyId = "key_q";
        kit.ActionMenuVm.Center = new PhysicalPoint(10, 10);
        Pump();

        Check.That(fired).IsEqualTo(0);
    }

    [Fact]
    public void Source_SubscribesToolbarVisibilityThroughTheBridgeCoordinator()
    {
        // Toolbar slide-in/out and auto-hide arrive as ToolbarLifecycleCoordinator.ToolbarVisibilityChanged
        // (the adapter fires it on real transitions). Driving the whole adapter chain headlessly
        // needs a laid-out toolbar window, so this is the repo's source-pin discipline instead: the
        // subscription must exist in the inventory's own wiring, taken from the bridge's attached
        // coordinator at construction (the same strictly-before-first-enable argument the preview
        // service's suspension holder uses).
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "GazeConsumerInventory.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "The inventory no longer subscribes ToolbarVisibilityChanged — a toolbar that " +
                "slides or auto-hides while the panel is up would leave the re-check blind to the " +
                "geometry change.")
            .That(source.Contains("ToolbarVisibilityChanged+=OnToolbarVisibilityChanged;")).IsTrue();
    }

    [Fact]
    public void Source_HooksKeyboardLayoutEventsInsideTheLiveKeyboardBranch()
    {
        // Final review F2: the keyboard window's SizeChanged/Loaded are Changed triggers (a
        // keyboard RELAYOUT raises no VM property flip — the prediction bar's growth while
        // typing). They are hooked lazily inside Live()'s keyboard branch, because the overlays
        // factory is the lazy seam — headless construction must never resolve the window (the
        // default Kit's factory throws). Source-pin discipline as above: driving the real
        // window's SizeChanged headlessly is the service-level relayout test's job.
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "GazeConsumerInventory.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "The inventory no longer hooks the keyboard window's layout events — a keyboard " +
                "that relayouts into the panel's rect would leave the re-check blind to the " +
                "geometry change.")
            .That(source.Contains("HookKeyboardLayoutEvents(keyboard);")).IsTrue();
        Check.That(source.Contains("keyboard.SizeChanged+=OnKeyboardLayoutChanged;")).IsTrue();
        Check.That(source.Contains("keyboard.Loaded+=OnKeyboardLoaded;")).IsTrue();
    }

    [Fact]
    public void BridgeWithoutCoordinator_ConstructsWithoutThrowing()
    {
        // The bridge can legitimately be unattached only in a wiring-defect world (D14: the comfort
        // feature tolerates it — just without the toolbar-visibility trigger — rather than crashing
        // the first enable edge).
        var kit = new Kit();
        Check.ThatCode(() => kit.Build()).Not.ThrowsAny();
    }

    // Final review F2: pumps at ContextIdle — BELOW the deferred Changed raise's Background
    // priority. A Background-priority marker can end the pump before a raise posted from inside it
    // is reached (FIFO within a priority), which is exactly the coordinator-callback shape the
    // first-show tests drive.
    private static void Pump() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    /// <summary>
    /// The manager's Keyboard property is the concrete <see cref="KeyboardWindow"/>, so the
    /// keyboard seam cannot be faked; everything the inventory touches beyond it throws (the
    /// <c>TestFakes.PartialOverlayManager</c> discipline).
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
        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();
        public void CloseAll() => throw new NotImplementedException();
    }
}
