namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
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

    [Fact]
    public void ShowBackdrop_PublishesTheCornerAt53Dip_WhoseDiscAndHaloFitTheScreen()
    {
        // review-01 finding 3 (P1): the coordinator's corner arithmetic was restated by every gate,
        // so :108 could drift to 40.0 and 108 tests stayed green. This drives the real show path —
        // manual selection becoming visible, the same trigger the hint tests use — and pins what
        // production PUBLISHES: the ShowBackdropMessage centre the dwell target is registered from.
        var glassBackdrop = new TestFakes.FakeGlassBackdropOverlay();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var manualSelectionVm = new ManualSelectionOverlayViewModel(settings, messenger);

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

        ShowBackdropMessage? published = null;
        messenger.Register<ShowBackdropMessage>(this, (_, message) => published = message);

        manualSelectionVm.IsVisible = true;
        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.IsVisible));

        Check.WithCustomMessage("the show path published no ShowBackdropMessage.")
            .That(published).IsNotNull();
        Check.That(glassBackdrop.Calls).Contains("ShowBackdrop");

        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var dipX = DpiHelper.PhysicalToLogicalX(published!.HideButtonCenter.X);
        var dipY = DpiHelper.PhysicalToLogicalY(published.HideButtonCenter.Y);
        var screenDipWidth = screen.Width / DpiHelper.ScaleX;
        var screenDipHeight = screen.Height / DpiHelper.ScaleY;

        Check.WithCustomMessage(
                $"the ✕'s published centre ({dipX:0.##}, {dipY:0.##}) DIP is not 53 DIP from the right and top "
                + $"of the {screenDipWidth:0.#}×{screenDipHeight:0.#} DIP screen.")
            .That(dipX).IsCloseTo(screenDipWidth - 53, 0.01);
        Check.That(dipY).IsCloseTo(53, 0.01);

        // The choice's property, on the PUBLISHED centre: the 100-DIP disc (radius 50) and its
        // 105-DIP rim halo (radius 52.5) lie inside the screen.
        const double haloRadius = 52.5;
        Check.WithCustomMessage(
                $"the disc around the published centre reaches {dipX + UIConstants.ButtonRadius:0.#} DIP against the "
                + $"screen's {screenDipWidth:0.#} DIP; it must be inside.")
            .That(dipX + UIConstants.ButtonRadius).IsStrictlyLessThan(screenDipWidth);
        Check.That(dipX + haloRadius).IsStrictlyLessThan(screenDipWidth);
        Check.WithCustomMessage(
                $"the halo around the published centre reaches {dipY - haloRadius:0.#} DIP above the screen's top edge.")
            .That(dipY - haloRadius).IsStrictlyGreaterThan(0);
        Check.That(dipY + haloRadius).IsStrictlyLessThan(screenDipHeight);
        Check.That(dipX - haloRadius).IsStrictlyGreaterThan(0);
    }

    // (The Paused ring dim (A-M2) test was removed with the dim plumbing itself: PR #191 hides the
    // ring in Paused and no production path shows the window in that state, so there is no dim call
    // left to wire. The VM's IsVisible flip is covered by the VM's own tests; the coordinator step
    // that forwards it to the window — the ONLY thing hiding the ring while paused — is pinned by
    // CursorPausedState_HidesAndShowsTheRing below, which replaces the deleted dim test.)

    [Fact]
    public void CursorPausedState_HidesAndShowsTheRing()
    {
        // Review round 1 (M3): deleting the `else cursorCircle.Hide()` arm of the IsVisible case
        // survived every suite green — the deleted dim test had been the only App-level test
        // driving Paused through this coordinator. The hide is the paused ring's whole mechanism
        // now (PR #191 + the dim deletion), so the forwarding gets its own test: the real state
        // message flips the VM's IsVisible, and the coordinator's handler must forward Hide, then
        // Show on resume. Mutation: drop the else arm → HideCalls stays empty, red.
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

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        Check.That(cursorCircle.HideCalls).IsEqualTo(1);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        Check.That(cursorCircle.ShowCalls).IsEqualTo(1);
        Check.That(cursorCircle.HideCalls).IsEqualTo(1);
    }

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

    /// <summary>Private nested fake (repo convention: no mocking library): records the pins, moves,
    /// and the show/hide forwarding (the paused ring's whole mechanism).</summary>
    private sealed class RecordingCursorCircle : ICursorCircleOverlay
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public List<bool> PinnedCalls { get; } = new();
        public List<(PhysicalPoint Target, bool Glide)> MoveCalls { get; } = new();

        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
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

    // ── W2 (a): the probe owns the ring while ProbeOpen (build plan C9) ──
    //
    // The gate exists so nothing re-shows the real cursor ring over the probe's opaque window. The
    // test drives the real handler directly (the nine-VM Initialize path is irrelevant to it) and
    // kills the "drop the ProbeOpen term" mutation: with the term gone the flip below calls Show().

    [Fact]
    public void WhenProbeOpen_ACursorVisibilityFlipDoesNotShowTheRing()
    {
        var modal = new ModalInteractionState { ProbeOpen = true };
        var ring = new RecordingRingVisibility();
        var messenger = new WeakReferenceMessenger();
        var cursorVm = new CursorCircleViewModel(new EyeCtrlSettings(), messenger) { IsVisible = false };
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            CursorVM = cursorVm,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(cursorCircle: ring),
        };

        var coordinator = new OverlayVmBindingCoordinator(modal);
        SetContext(coordinator, ctx);

        cursorVm.IsVisible = true;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));

        Check.WithCustomMessage("The ring was re-shown over the probe window")
            .That(ring.ShowCalls).IsEqualTo(0);
        Check.That(ring.HideCalls).IsEqualTo(1);

        // Negative control: with the probe down the same flip shows the ring again.
        modal.ProbeOpen = false;
        cursorVm.IsVisible = false;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        cursorVm.IsVisible = true;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));

        Check.That(ring.ShowCalls).IsEqualTo(1);
    }

    /// <summary>Records ring Show/Hide so the probe gate is observable without a window.</summary>
    private sealed class RecordingRingVisibility : ICursorCircleOverlay
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
