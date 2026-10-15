namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
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
