namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
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

    // (The Paused ring dim (A-M2) test was removed with the dim plumbing itself: PR #191 hides the
    // ring in Paused and no production path shows the window in that state, so there is no dim call
    // left to wire — the VM hides via the IsVisible case, which its own tests cover.)

    [Fact]
    public void CursorPinAndGlide_AreForwardedToTheRing_AsOneMoveCall()
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

        // A pin is Core's accepted settle, not the activation button's show (ticket 7). The onset's
        // StateChanged precedes it; the VM accepts a pin only in Fixation (review A, F4).
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 0));
        messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        messenger.Send(new PinRingMessage(new PhysicalPoint(300, 200)));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsPinned));
        Check.That(cursorCircle.PinnedCalls).ContainsExactly(true);

        // The first frame after the pin starts the glide: ONE call carrying the target and the flag,
        // so the window never applies a position before it knows the jump is a pin (M3/N10).
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(105, 100), 30_000));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.MoveTo));
        Check.That(cursorCircle.MoveCalls).ContainsExactly((new PhysicalPoint(100, 100), true));

        // A further glide frame: the interpolated position, still flagged.
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(110, 100), 60_000));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.MoveTo));
        Check.That(cursorCircle.MoveCalls).HasSize(2);
        Check.That(cursorCircle.MoveCalls[1].Glide).IsTrue();
        Check.That(cursorCircle.MoveCalls[1].Target.X).IsStrictlyGreaterThan(100);
        Check.That(cursorCircle.MoveCalls[1].Target.X).IsStrictlyLessThan(300);

        // Release: unpin. The snap to gaze arrives on the ordinary Position channel.
        messenger.Send(new ReleaseRingMessage());
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsPinned));
        Check.That(cursorCircle.PinnedCalls).ContainsExactly(true, false);
    }

    [Fact]
    public void ActivationVisible_PlacesTheHaloOnTheOfferButton_SizedForTheHitRadius()
    {
        // Ticket 8 at the wiring level: the halo is centred on the OFFER's B and sized from the offer's
        // hit radius — both carried on the show message. The fixation radius setting (75) and the ring
        // size (250) are deliberately different from what the message carries, so reading either
        // instead of the message goes red.
        var activationButton = new RecordingActivationButton();
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = 75;
        settings.Cursor.CircleSize = 250;
        var messenger = new WeakReferenceMessenger();
        var activationVm = new ActivationButtonViewModel(messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ActivationVM = activationVm,
            Settings = settings,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                activationButton: activationButton,
                actionMenu: new TestFakes.FakeActionMenuOverlay()),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);

        var buttonCenter = new PhysicalPoint(1200, 800);
        messenger.Send(new ShowActivationButtonMessage(buttonCenter, LayoutConstants.ActivationButtonHitRadiusPx));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));

        Check.That(activationButton.ShownAt).HasSize(1);
        var (position, radiusPixels) = activationButton.ShownAt[0];
        Check.That(position.X).IsEqualTo(buttonCenter.X);
        Check.That(position.Y).IsEqualTo(buttonCenter.Y);
        Check.That(radiusPixels).IsEqualTo(LayoutConstants.ActivationButtonHitRadiusPx);
    }

    [Fact]
    public void ActivationVisible_PositionsTheHaloBeforeShowingIt()
    {
        // The stale-flash order (A-F4): Show() first presents the window where the PREVIOUS show left
        // it, at the previous radius, for a frame. Positioning must come first.
        var activationButton = new RecordingActivationButton();
        var messenger = new WeakReferenceMessenger();
        var activationVm = new ActivationButtonViewModel(messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ActivationVM = activationVm,
            Settings = new EyeCtrlSettings(),
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                activationButton: activationButton,
                actionMenu: new TestFakes.FakeActionMenuOverlay()),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);

        messenger.Send(new ShowActivationButtonMessage(
            new PhysicalPoint(1200, 800), LayoutConstants.ActivationButtonHitRadiusPx));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));

        Check.That(activationButton.Calls).ContainsExactly("ShowAt", "Show");
    }

    private static void InvokeCursorHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnCursorVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }

    private static void InvokeActivationHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnActivationVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }

    /// <summary>Private nested fake (repo convention: no mocking library): records the pins and moves.</summary>
    private sealed class RecordingCursorCircle : ICursorCircleOverlay
    {
        public List<bool> PinnedCalls { get; } = new();
        public List<(PhysicalPoint Target, bool Glide)> MoveCalls { get; } = new();

        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) => MoveCalls.Add((point, glide));
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) => PinnedCalls.Add(pinned);
    }

    /// <summary>Records where the halo was placed, the radius it was sized for, and the call order.</summary>
    private sealed class RecordingActivationButton : IActivationButtonOverlay
    {
        public List<(PhysicalPoint Position, double RadiusPixels)> ShownAt { get; } = new();
        public List<string> Calls { get; } = new();

        public void Show() => Calls.Add("Show");

        public void ShowAt(PhysicalPoint position, double fixationRadiusPixels)
        {
            Calls.Add("ShowAt");
            ShownAt.Add((position, fixationRadiusPixels));
        }

        public void Hide() { }
        public void HideImmediate() => Calls.Add("HideImmediate");
        public void UpdateDwellProgress(double progress) { }
    }

    [Fact]
    public void Replacement_CloseThenShow_RepositionsTheHalo()
    {
        // Review A A-F4/C18: Core closes the old offer before showing the new one, and the coordinator's
        // IsVisible edge must reposition the window at the new B. Mutation: drop the close → see
        // ButtonCenterChange_WhileVisible_RepositionsTheHalo, which pins the belt-and-braces case.
        var activationButton = new RecordingActivationButton();
        var messenger = new WeakReferenceMessenger();
        var activationVm = new ActivationButtonViewModel(messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ActivationVM = activationVm,
            Settings = new EyeCtrlSettings(),
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                activationButton: activationButton,
                actionMenu: new TestFakes.FakeActionMenuOverlay()),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);

        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(100, 100), 150));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));

        // The replacement: close, then show at the new centre.
        messenger.Send(new HideActivationButtonMessage());
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));
        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(700, 500), 150));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));

        Check.That(activationButton.ShownAt).HasSize(2);
        Check.That(activationButton.ShownAt[1].Position.X).IsEqualTo(700);
        Check.That(activationButton.ShownAt[1].Position.Y).IsEqualTo(500);
        Check.That(activationButton.Calls)
            .ContainsExactly("ShowAt", "Show", "HideImmediate", "ShowAt", "Show");
    }

    [Fact]
    public void ButtonCenterChange_WhileVisible_RepositionsTheHalo()
    {
        // The belt-and-braces half of C18: if a future path changes the offer's B without a visibility
        // edge, the halo must still follow. Mutation: drop the ButtonCenter case → only one ShowAt.
        var activationButton = new RecordingActivationButton();
        var messenger = new WeakReferenceMessenger();
        var activationVm = new ActivationButtonViewModel(messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ActivationVM = activationVm,
            Settings = new EyeCtrlSettings(),
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                activationButton: activationButton,
                actionMenu: new TestFakes.FakeActionMenuOverlay()),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);

        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(100, 100), 150));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsVisible));
        Check.That(activationButton.ShownAt).HasSize(1);

        // No close: the VM is already visible and only the centre moves (Receive(Show) sets the centre
        // before it re-assigns IsVisible, which is a no-change for an already-visible button).
        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(400, 400), 150));
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.ButtonCenter));

        Check.That(activationButton.ShownAt).HasSize(2);
        Check.That(activationButton.ShownAt[1].Position.X).IsEqualTo(400);
    }

    private static void InvokeManualSelectionHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnManualSelectionVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }
}
