namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

public class SettingsUiCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, a
    // ToolbarLifecycleCoordinator, and a KeyboardCoordinator. None of these are touched by the
    // lifecycle contract tests below (they never call Initialize), so plain constructed instances
    // suffice — the same shape the sibling coordinator tests use. ONE shared ModalInteractionState
    // is threaded through every coordinator that takes one, matching the production wiring and
    // avoiding a trap for future flag-propagation tests (distinct instances would silently mask a
    // coordinator that failed to observe a sibling's flag flip).
    private static SettingsUiCoordinator NewCoordinator()
    {
        var modal = new ModalInteractionState();
        return new(modal,
            new QuickActionsCoordinator(modal),
            new ToolbarLifecycleCoordinator(modal),
            new KeyboardCoordinator(modal,
                new QuickActionsCoordinator(modal),
                new ToolbarLifecycleCoordinator(modal)));
    }

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = NewCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized coordinator
        // must still dispose cleanly (no NullRef on _context).
        NewCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = NewCoordinator();
        c.Dispose();
        c.Dispose();
    }

    [Fact]
    public void Ctor_RejectsNullCollaborators()
    {
        var modal = new ModalInteractionState();
        var quickActions = new QuickActionsCoordinator(modal);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);

        Check.ThatCode(() => new SettingsUiCoordinator(null!, quickActions, toolbar, keyboard)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new SettingsUiCoordinator(modal, null!, toolbar, keyboard)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new SettingsUiCoordinator(modal, quickActions, null!, keyboard)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new SettingsUiCoordinator(modal, quickActions, toolbar, null!)).Throws<ArgumentNullException>();
    }

    // ── Prediction-bar refresh gate (review round 2, finding 1) ─────────────────────────────
    // The inline version of this decision shipped with zero executable coverage — deleting the
    // whole block broke no test — so the predicate is extracted and pinned here, the same remedy
    // ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample received for the same discovery.

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void ShouldRefreshPredictionBar_DecidesOnReloadAndKeyboardVisibility(
        bool reloaded, bool keyboardVisible, bool expected)
    {
        Check.That(SettingsUiCoordinator.ShouldRefreshPredictionBar(reloaded, keyboardVisible))
            .IsEqualTo(expected);
    }

    // ── Task 9: the live Enabled-flip rebuild (ReinitializeToolbar + the PropertyChanged seam) ──
    //
    // The settings UI mutates the live settings object as the user toggles, so an
    // AdaptationSettings.Enabled flip arrives as PropertyChanged BEFORE any save/SettingsChanged.
    // The band must follow the flip immediately (Reset appears/disappears, D5/M5), and the
    // save-path re-init that follows an Apply must not rebuild the band a second time for the
    // same state. Initialized through the shared TestFakes context (real dispatcher + messenger
    // + interaction manager; the toolbar is a recording fake — a real ToolbarWindow is
    // ToolbarBandRenderedButtonsTests' territory).

    /// <summary>Records Initialize calls; everything else is a no-op — only band membership
    /// (includeReset) and its metrics are what this suite pins.</summary>
    private sealed class RecordingToolbar : IToolbarOverlay
    {
        public List<(double ButtonSize, int DwellMs, bool IncludeReset)> InitializeCalls { get; } = new();

        public bool IsVisible => false;
        public bool TargetsInitialized => false;
        public bool IsSliding => false;
        public bool IsOnScreen => false;
        public double ExtentDip => 0;

        public void Show() { }
        public void Hide() { }

        public void Initialize(double buttonSize, int dwellTimeMs, bool includeReset) =>
            InitializeCalls.Add((buttonSize, dwellTimeMs, includeReset));

        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public void ShowAtRest() { }
        public void ShowThenSlideIn() { }
        public void SlideOutThenHide(Action onHidden) { }
        public PhysicalRect GetPhysicalBounds() => new(0, 1040, 1920, 40);
    }

    private sealed class InitializedCoordinator : IDisposable
    {
        public SettingsUiCoordinator Coordinator { get; }
        public RecordingToolbar Toolbar { get; } = new();
        public EyeCtrlSettings Settings { get; } = new();
        public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;

        public InitializedCoordinator()
        {
            var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                Settings = Settings,
                Dispatcher = Dispatcher,
                Messenger = new WeakReferenceMessenger(),
                InteractionManager = new InteractionManager(),
                OverlayManager = TestFakes.MakeOverlayManager(
                    toolbar: Toolbar,
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    trackStatus: new TestFakes.FakeTrackStatusOverlay()),
            };
            var modal = new ModalInteractionState();
            Coordinator = new SettingsUiCoordinator(modal,
                new QuickActionsCoordinator(modal),
                new ToolbarLifecycleCoordinator(modal),
                new KeyboardCoordinator(modal,
                    new QuickActionsCoordinator(modal),
                    new ToolbarLifecycleCoordinator(modal)));
            Coordinator.Initialize(ctx);
        }

        public void Dispose() => Coordinator.Dispose();
    }

    /// <summary>Drives the save path: production wires OnSettingsChanged to
    /// SettingsWindow.SettingsChanged, which no headless test can construct. Reflection invoke is
    /// this repo's established pattern for exactly this (SupervisorTrayIconTests raises the menu's
    /// OnOpening the same way).</summary>
    private static void ApplySettingsSave(SettingsUiCoordinator coordinator, EyeCtrlSettings settings)
    {
        typeof(SettingsUiCoordinator)
            .GetMethod("OnSettingsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [settings]);
    }

    /// <summary>Drains the dispatcher queue: run everything queued at/above Background priority.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void AdaptationEnabledFlip_RebuildsTheBand_WithTheLiveFlag()
    {
        using var c = new InitializedCoordinator();

        c.Settings.Adaptation.Enabled = true;
        Pump(c.Dispatcher);

        Check.That(c.Toolbar.InitializeCalls).HasSize(1);
        Check.That(c.Toolbar.InitializeCalls[0].IncludeReset).IsTrue();

        c.Settings.Adaptation.Enabled = false;
        Pump(c.Dispatcher);

        Check.That(c.Toolbar.InitializeCalls).HasSize(2);
        Check.That(c.Toolbar.InitializeCalls[1].IncludeReset).IsFalse();
    }

    [StaFact]
    public void AdaptationEnabledFlip_IsMarshalledThroughTheDispatcher()
    {
        // The flip may be raised on whatever thread mutated the property; the rebuild touches
        // overlay windows, so it must land on the UI thread's dispatcher, not run inline.
        using var c = new InitializedCoordinator();

        c.Settings.Adaptation.Enabled = true;
        Check.That(c.Toolbar.InitializeCalls).IsEmpty();

        Pump(c.Dispatcher);
        Check.That(c.Toolbar.InitializeCalls).HasSize(1);
    }

    [StaFact]
    public void NonEnabledAdaptationChange_DoesNotRebuildTheBand()
    {
        using var c = new InitializedCoordinator();

        c.Settings.Adaptation.Posture = AdaptationPosture.Balanced;
        Pump(c.Dispatcher);

        Check.That(c.Toolbar.InitializeCalls).IsEmpty();
    }

    [StaFact]
    public void ToolbarMetricsSave_ReinitializesOnce_UnchangedByTheFlipWiring()
    {
        // The pre-Task-9 behavior, pinned so the factoring cannot lose it: a save that changes a
        // toolbar metric rebuilds the band exactly once, Enabled untouched.
        using var c = new InitializedCoordinator();

        c.Settings.Toolbar.Size += 8;
        ApplySettingsSave(c.Coordinator, c.Settings);
        Pump(c.Dispatcher);

        Check.That(c.Toolbar.InitializeCalls).HasSize(1);
    }

    [StaFact]
    public void ToolbarChangeAndEnabledFlipInTheSameSave_ReinitializeExactlyOnce_FlipFirst()
    {
        // Production order: the UI mutates live (the flip queues a rebuild via PropertyChanged),
        // then ApplyAndSave raises SettingsChanged. The queued rebuild applies the new metrics AND
        // the new flag; the save's snapshot branch must recognize the state as already applied.
        using var c = new InitializedCoordinator();

        c.Settings.Toolbar.Size += 8;
        c.Settings.Adaptation.Enabled = true;
        Pump(c.Dispatcher);
        Check.That(c.Toolbar.InitializeCalls).HasSize(1);

        ApplySettingsSave(c.Coordinator, c.Settings);
        Pump(c.Dispatcher);

        Check.WithCustomMessage(
                "A toolbar-metrics change plus an adaptation flip in one save rebuilt the band "
                + "more than once.")
            .That(c.Toolbar.InitializeCalls).HasSize(1);
        Check.That(c.Toolbar.InitializeCalls[0].IncludeReset).IsTrue();
    }

    [StaFact]
    public void ToolbarChangeAndEnabledFlipInTheSameSave_ReinitializeExactlyOnce_SaveFirst()
    {
        // The guard must not depend on sequencing: if the save's rebuild runs before the queued
        // flip callback (or the flip is mutated without a pump in between), the callback must
        // find the state already applied and no-op — and a later flip back must still rebuild,
        // proving the guard remembers BOTH terms (metrics and flag), not just the snapshot.
        using var c = new InitializedCoordinator();

        c.Settings.Toolbar.Size += 8;
        c.Settings.Adaptation.Enabled = true;

        ApplySettingsSave(c.Coordinator, c.Settings);
        Check.That(c.Toolbar.InitializeCalls).HasSize(1);

        Pump(c.Dispatcher);   // the queued flip callback lands after the save's rebuild
        Check.WithCustomMessage(
                "The queued Enabled-flip callback rebuilt the band a second time for state the " +
                "save path had already applied.")
            .That(c.Toolbar.InitializeCalls).HasSize(1);

        c.Settings.Adaptation.Enabled = false;
        Pump(c.Dispatcher);
        Check.That(c.Toolbar.InitializeCalls).HasSize(2);
        Check.That(c.Toolbar.InitializeCalls[1].IncludeReset).IsFalse();
    }

    [StaFact]
    public void FlipAfterDispose_DoesNotReachTheToolbar()
    {
        // The subscription must die with the coordinator (the class's TrackStatus precedent):
        // the settings object outlives it, so a stale handler would rebuild the band forever.
        var c = new InitializedCoordinator();
        var toolbar = c.Toolbar;
        var settings = c.Settings;
        var dispatcher = c.Dispatcher;
        c.Dispose();

        settings.Adaptation.Enabled = true;
        Pump(dispatcher);

        Check.That(toolbar.InitializeCalls).IsEmpty();
    }

    // ── W2 (b): Settings' close path must not fight the probe (build plan C9) ─────────────────
    //
    // The settings-close handling re-suspends auto-hide and normally un-suspends the summon and
    // re-shows the ring. While the probe is up all three would undo what the probe just parked, so
    // the :263/:265 gates keep them inert. Killed mutations: drop the summon gate → the summon
    // resumes; drop the ring gate → Show() runs.

    [Fact]
    public void WhenProbeOpen_SettingsCloseLeavesTheSummonSuspended_AndDoesNotShowTheRing()
    {
        var modal = new ModalInteractionState { ProbeOpen = true };
        var quickActions = new QuickActionsCoordinator(modal);
        var summon = new EyeCtrl.App.Services.QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        quickActions.InjectSummonForTesting(summon);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);
        var coordinator = new SettingsUiCoordinator(modal, quickActions, toolbar, keyboard);

        var ring = new RecordingRingVisibility();
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Settings = settings,
            Messenger = messenger,
            CursorVM = new CursorCircleViewModel(settings, messenger) { IsVisible = true },
            OverlayManager = TestFakes.MakeOverlayManager(
                cursorCircle: ring, trackStatus: new TestFakes.FakeTrackStatusOverlay()),
        };
        coordinator.Initialize(context);
        quickActions.Initialize(context);
        // The probe's BeginStart suspended the summon before the probe window appeared.
        quickActions.SetSummonSuspended(true);
        Check.That(quickActions.IsSummonSuspended).IsTrue();

        coordinator.OnSettingsWindowClosed(null, EventArgs.Empty);

        Check.WithCustomMessage("The :263 summon gate is gone: Settings' close resumed the probe's summon")
            .That(quickActions.IsSummonSuspended).IsTrue();
        Check.WithCustomMessage("The :265 ring gate is gone: the ring was re-shown over the probe")
            .That(ring.ShowCalls).IsEqualTo(0);

        // Negative control: with the probe down, the same close resumes the summon and shows the ring.
        modal.ProbeOpen = false;
        coordinator.OnSettingsWindowClosed(null, EventArgs.Empty);

        Check.That(quickActions.IsSummonSuspended).IsFalse();
        Check.That(ring.ShowCalls).IsEqualTo(1);

        coordinator.Dispose();
        quickActions.Dispose();
    }

    /// <summary>Records ring Show/Hide so the close-path gates are observable without a window.</summary>
    private sealed class RecordingRingVisibility : EyeCtrl.App.Services.ICursorCircleOverlay
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }

        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPausedDim(bool dimmed) { }
        public void SetPinned(bool pinned) { }
    }
}
