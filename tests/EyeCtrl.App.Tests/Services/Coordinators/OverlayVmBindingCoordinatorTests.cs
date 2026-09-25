namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class OverlayVmBindingCoordinatorTests
{
    // The ctor needs a ModalInteractionState (the cursor-guard reads _modal.SettingsOpen). It is
    // never touched by the lifecycle contract tests below (they never call Initialize), so a plain
    // constructed instance suffices — the same shape the sibling coordinator tests use.
    private static OverlayVmBindingCoordinator NewCoordinator() =>
        new(new ModalInteractionState());

    private static void SetContext(OverlayVmBindingCoordinator coordinator, EventCoordinationContext context) =>
        typeof(CoordinatorBase)
            .GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, context);

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
    public void Ctor_RejectsNullModalState()
    {
        Check.ThatCode(() => new OverlayVmBindingCoordinator(null!)).Throws<ArgumentNullException>();
    }

    // ── Manual-selection escape hint (ticket: the magnifier is the one manual-selection view
    // that currently renders no textual sign that ESC works) ──
    //
    // OnManualSelectionVMPropertyChanged's full Initialize() path eagerly subscribes NINE
    // ViewModels this test does not exercise (Cursor/Activation/ManualSelectionIndicator/
    // ActionMenu/Toolbar/Backdrop/TaskSwitcher/ModesMenu/RepeatCursor), some non-trivial to
    // construct (ToolbarViewModel). Reflection sets Context and the private _glassBackdrop field
    // directly and invokes the handler in isolation — the same "reflect private state, skip the
    // expensive/irrelevant setup path" convention this project already uses for STA window tests
    // (see sta-window-tests-are-cheap memory / GlassBackdropWindowTests).

    [Fact]
    public void ManualSelectionBecomesVisible_ShowsTheEscapeHint()
    {
        var glassBackdrop = new TestFakes.FakeGlassBackdropOverlay();
        var coordinator = BuildCoordinatorForManualSelectionHint(glassBackdrop, out var manualSelectionVm);

        manualSelectionVm.IsVisible = true;
        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.IsVisible));

        Check.That(glassBackdrop.EscapeHintVisible).IsTrue();
    }

    [Fact]
    public void ManualSelectionBecomesHidden_HidesTheEscapeHint()
    {
        var glassBackdrop = new TestFakes.FakeGlassBackdropOverlay();
        var coordinator = BuildCoordinatorForManualSelectionHint(glassBackdrop, out var manualSelectionVm);

        manualSelectionVm.IsVisible = true;
        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.IsVisible));
        Check.That(glassBackdrop.EscapeHintVisible).IsTrue();

        manualSelectionVm.IsVisible = false;
        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.IsVisible));

        Check.That(glassBackdrop.EscapeHintVisible).IsFalse();
    }

    private static OverlayVmBindingCoordinator BuildCoordinatorForManualSelectionHint(
        TestFakes.FakeGlassBackdropOverlay glassBackdrop,
        out ManualSelectionOverlayViewModel manualSelectionVm)
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        manualSelectionVm = new ManualSelectionOverlayViewModel(settings, messenger);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ManualSelectionVM = manualSelectionVm,
            Settings = settings,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                activationButton: new TestFakes.FakeActivationButtonOverlay(),
                manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                glassBackdrop: glassBackdrop),
        };

        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);
        typeof(OverlayVmBindingCoordinator)
            .GetField("_glassBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, glassBackdrop);

        return coordinator;
    }

    // ── Paused ring dim (A-M2) ──
    // The dim call was wired but had no end-to-end test: deleting the IsPaused case in
    // OnCursorVMPropertyChanged left the suite green while the ring stopped dimming on pause.

    [Fact]
    public void CursorPausedState_DimsTheRing_AndUndimsOnResume()
    {
        var cursorCircle = new RecordingCursorCircle();
        var messenger = new WeakReferenceMessenger();
        var cursorVm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            CursorVM = cursorVm,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(cursorCircle: cursorCircle),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);

        // Drive the real state message: the VM flips IsPaused, and the coordinator's handler — the
        // wiring under test — forwards it to the ring.
        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsPaused));
        Check.That(cursorCircle.PausedDimCalls).ContainsExactly(true);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsPaused));
        Check.That(cursorCircle.PausedDimCalls).ContainsExactly(true, false);
    }

    private static void InvokeCursorHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnCursorVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }

    /// <summary>Private nested fake (repo convention: no mocking library): records the pause dims.</summary>
    private sealed class RecordingCursorCircle : ICursorCircleOverlay
    {
        public List<bool> PausedDimCalls { get; } = new();

        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPausedDim(bool dimmed) => PausedDimCalls.Add(dimmed);
    }

    private static void InvokeManualSelectionHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnManualSelectionVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }
}
