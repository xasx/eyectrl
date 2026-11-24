namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
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

    // F2 (magnifier-default-zoom fixup 01): pin the REAL chain, not a direct call. The adapter's
    // start message (the fit's 81 DIP on a 720-DIP screen) flows through the real
    // ManualSelectionOverlayViewModel into this coordinator and out to the overlay: BOTH the Show
    // and the recapture UpdateCenter must carry 81, never the raw setting (100). Mutants: either
    // branch reading Context.Settings.ManualSelection.CircleRadiusPixels instead of VM.Radius.

    [Fact]
    public void ManualSelectionStart_OnA720DipScreen_TheRealChainCarriesTheEffectiveRadius()
    {
        var settings = new EyeCtrlSettings();
        settings.ManualSelection.CircleRadiusPixels = 100;   // the raw setting: must NOT reach the window
        settings.ManualSelection.MagnificationLevel = 4.0;
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionOverlayViewModel(settings, messenger);
        var recording = new RecordingManualSelectionOverlay();
        var glassBackdrop = new TestFakes.FakeGlassBackdropOverlay();

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            ManualSelectionVM = vm,
            InteractionManager = new EyeCtrl.Core.Processing.InteractionManager(),
            Settings = settings,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                activationButton: new TestFakes.FakeActivationButtonOverlay(),
                manualSelection: recording,
                manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                glassBackdrop: glassBackdrop),
        };

        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);
        typeof(OverlayVmBindingCoordinator)
            .GetField("_glassBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, glassBackdrop);

        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(640, 360), 81, 4.0, new PhysicalPoint(640, 360)));
        Check.That(vm.Radius).IsEqualTo(81);

        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.IsVisible));

        Check.WithCustomMessage($"the magnifier must be shown once; saw {recording.Shows.Count}")
            .That(recording.Shows).HasSize(1);
        Check.WithCustomMessage($"Show must carry the message radius; saw {recording.Shows[0].Radius}")
            .That(recording.Shows[0].Radius).IsEqualTo(81);
        Check.That(recording.Shows[0].Magnification).IsEqualTo(4.0);

        vm.RequestRecapture();
        InvokeManualSelectionHandler(coordinator, nameof(ManualSelectionOverlayViewModel.RecaptureVersion));

        Check.WithCustomMessage($"the recapture must reposition once; saw {recording.UpdateCenters.Count}")
            .That(recording.UpdateCenters).HasSize(1);
        Check.WithCustomMessage($"the recapture UpdateCenter must carry 81; saw {recording.UpdateCenters[0].Radius}")
            .That(recording.UpdateCenters[0].Radius).IsEqualTo(81);
    }

    /// <summary>The magnifier, recording what the coordinator hands it (F2).</summary>
    private sealed class RecordingManualSelectionOverlay : IManualSelectionOverlay
    {
        public List<(PhysicalPoint Center, int Radius, double Magnification)> Shows { get; } = [];
        public List<(PhysicalPoint Center, int Radius, double Magnification)> UpdateCenters { get; } = [];

        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true)
            => Shows.Add((center, radius, magnification));

        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true)
            => UpdateCenters.Add((center, radius, magnification));

        public void Hide() { }

        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;

        public PhysicalPoint EffectiveCaptureCenter =>
            throw new NotImplementedException("this recording overlay does not exercise the capture centre");
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
            // The hide branch reads the state for its Pause fallback (the "closed by" log).
            InteractionManager = new EyeCtrl.Core.Processing.InteractionManager(),
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
        // it, at the previous radius, for a frame. Positioning must come first, and the held flag is
        // set before the window is shown (B2 F1).
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

        Check.That(activationButton.Calls).ContainsExactly("ShowAt", "SetFillPaused", "Show");
        Check.WithCustomMessage("the show path must start un-dimmed")
            .That(activationButton.FillPaused).IsEqualTo(false);
    }

    [Fact]
    public void ActivationPausedFlip_ReachesTheWindow()
    {
        // B2 F1: the held fill's dim is driven from the VM's derived flag. Mutation: drop the
        // IsFillPaused branch → the fake never sees a pause.
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

        activationVm.IsFillPaused = true;
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsFillPaused));
        Check.WithCustomMessage("the held flag did not reach the window")
            .That(activationButton.FillPaused).IsEqualTo(true);

        activationVm.IsFillPaused = false;
        InvokeActivationHandler(coordinator, nameof(ActivationButtonViewModel.IsFillPaused));
        Check.That(activationButton.FillPaused).IsEqualTo(false);
    }

    // C5 H3: the backdrop VM's held-dwell flag reaches the window. The window's own remembered
    // value (GlassBackdropWindow.SetHideButtonPaused / DrawHideButton) carries it across a redraw;
    // the show path needs no re-apply because the ShowBackdropMessage resets the VM in the same
    // synchronous call (review-01 L2).
    [Fact]
    public void BackdropPausedCue_IsForwardedToTheWindow()
    {
        var glassBackdrop = new TestFakes.FakeGlassBackdropOverlay();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var backdropVm = new GlassBackdropViewModel(settings, messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            BackdropVM = backdropVm,
            Settings = settings,
            Messenger = messenger,
            OverlayManager = TestFakes.MakeOverlayManager(glassBackdrop: glassBackdrop),
        };
        var coordinator = NewCoordinator();
        SetContext(coordinator, ctx);
        typeof(OverlayVmBindingCoordinator)
            .GetField("_glassBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, glassBackdrop);

        backdropVm.IsDwellPaused = true;
        InvokeBackdropHandler(coordinator, nameof(GlassBackdropViewModel.IsDwellPaused));
        Check.WithCustomMessage("the VM's held-dwell flag did not reach the window")
            .That(glassBackdrop.HideButtonPaused).IsTrue();

        backdropVm.IsDwellPaused = false;
        InvokeBackdropHandler(coordinator, nameof(GlassBackdropViewModel.IsDwellPaused));
        Check.That(glassBackdrop.HideButtonPaused).IsFalse();
    }

    private static void InvokeBackdropHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnBackdropVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
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
        public bool? FillPaused { get; private set; }

        public void Show() => Calls.Add("Show");

        public void ShowAt(PhysicalPoint position, double fixationRadiusPixels)
        {
            Calls.Add("ShowAt");
            ShownAt.Add((position, fixationRadiusPixels));
        }

        public void Hide() { }
        public void HideImmediate() => Calls.Add("HideImmediate");
        public void UpdateDwellProgress(double progress) { }

        public void SetFillPaused(bool paused)
        {
            Calls.Add("SetFillPaused");
            FillPaused = paused;
        }
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
        Check.That(activationButton.Calls).ContainsExactly(
            "ShowAt", "SetFillPaused", "Show",
            "HideImmediate",
            "ShowAt", "SetFillPaused", "Show");
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

    // ── the first-run guide owns the screen: the ring must not re-show over it ──
    //
    // Same gate family as the probe above, but driven by the guide's modal flag. The flag is the
    // re-show seam the calibration sites in OverlayMessageCoordinator consult through _vmBinding;
    // the full ring lifecycle lives in the guide's own wiring (Task 12).

    [Fact]
    public void RingMayReshow_IsFalse_WhileGuideOpen()
    {
        var modal = new ModalInteractionState();
        var vmBinding = new OverlayVmBindingCoordinator(modal);

        modal.GuideOpen = true;
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.GuideOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();
    }

    [Fact]
    public void RingMayReshow_IsFalse_WhileBackstageOpen()
    {
        // The fullscreen Backstage menu is a hard stop, like the guide without any of the guide's
        // exceptions: no reactive path may paint the ring over another surface's screen. Mutation:
        // drop the `!_modal.BackstageOpen` term → the first assertion goes red.
        var modal = new ModalInteractionState();
        var vmBinding = new OverlayVmBindingCoordinator(modal);

        modal.BackstageOpen = true;
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.BackstageOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();

        // A guide that still wants its step-5 ring does not override another surface's screen.
        modal.GuideOpen = true;
        modal.GuideRingWanted = true;
        modal.BackstageOpen = true;
        Check.WithCustomMessage("the guide's step-5 exception must not paint the ring over Backstage.")
            .That(vmBinding.RingMayReshow).IsFalse();
    }

    [Fact]
    public void RingMayReshow_IsTrue_WhileTheGuidesStepWantsTheRing_EvenGuideOpen()
    {
        // S3-4: step 5 (ToolbarTask) shows the ring so the owner can summon the toolbar; a
        // pause/resume round trip during the task used to strand it hidden — the resume's
        // visibility flip consulted RingMayReshow, which refused while GuideOpen was still true.
        var modal = new ModalInteractionState();
        var vmBinding = new OverlayVmBindingCoordinator(modal);

        modal.GuideOpen = true;
        modal.GuideRingWanted = true;                    // the wiring sets it on the ToolbarTask step
        Check.That(vmBinding.RingMayReshow).IsTrue();
        modal.GuideRingWanted = false;                   // every other step / restore / unwind clears it
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.GuideOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();
    }

    [Fact]
    public void RingMayReshow_IsTrue_WhileTheGuidesCalibrationRuns_EvenGuideOpen()
    {
        // Ruling D3 (PR-review n1): the guide's calibration window is the second exception. The
        // guide is hidden and the calibration result card's Retry/Accept need the ring — the two
        // completion sites in OverlayMessageCoordinator re-show it through this very predicate,
        // which the guide's GuideOpen claim used to keep false for the whole guide lifetime (a
        // silently bad calibration auto-accepted after 12 s with Retry unreachable).
        var modal = new ModalInteractionState();
        var vmBinding = new OverlayVmBindingCoordinator(modal);

        modal.GuideOpen = true;
        Check.That(vmBinding.RingMayReshow).IsFalse();   // the guide owns the screen, no calibration
        modal.GuideCalibrationActive = true;             // Collecting through the result card
        Check.That(vmBinding.RingMayReshow).IsTrue();
        modal.GuideCalibrationActive = false;            // Idle/Cancelled: the guide's again
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.GuideOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();
    }

    [Fact]
    public void AResumeVisibilityFlip_DuringTheGuidesRingWantedStep_ShowsTheRing()
    {
        // The handler-level mirror of the flip above: the pause hides (any state, ungated — the
        // pause strip is the feedback), and the RESUME's flip re-shows while the guide is open and
        // step 5 wants the ring — exactly the strand this fix exists for.
        var modal = new ModalInteractionState { GuideOpen = true, GuideRingWanted = true };
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

        // The pause: the VM flips invisible, the ring hides.
        cursorVm.IsVisible = false;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        Check.That(ring.HideCalls).IsEqualTo(1);
        Check.That(ring.ShowCalls).IsEqualTo(0);

        // The resume: the VM flips visible again — GuideOpen still true, but the step wants the ring.
        cursorVm.IsVisible = true;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        Check.That(ring.ShowCalls).IsEqualTo(1);

        // Without the step-5 exception the same flip is a hide (the S1-era strand).
        cursorVm.IsVisible = false;
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        cursorVm.IsVisible = true;
        modal.GuideRingWanted = false;                   // e.g. the step just ended (or Restore ran)
        InvokeCursorHandler(coordinator, nameof(CursorCircleViewModel.IsVisible));
        Check.That(ring.ShowCalls).IsEqualTo(1);         // no second show
    }

    /// <summary>
    /// Fixup-01 item 3: the switcher's show/hide must suspend the toolbar auto-hide (the Settings
    /// pattern, named holder), so the band cannot hide during a long session and the close always
    /// finds it on screen to apply the fresh-entry guard.
    /// </summary>
    [Fact]
    public void SwitcherVisibility_SuspendsToolbarAutoHide_AndReleasesOnClose()
    {
        var modal = new ModalInteractionState();
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        toolbar.InjectAutoHideForTesting(new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: 1920, screenH: 1200,
            toolbarRect: new PhysicalRect(810, 0, 300, 50),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50));
        var switcher = new TaskSwitcherViewModel(windowManager: null!, settings, messenger);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Messenger = messenger,
            Settings = settings,
            Dispatcher = Dispatcher.CurrentDispatcher,
            TaskSwitcherVM = switcher,
            ToolbarVM = new ToolbarViewModel(settings, messenger),
            OverlayManager = TestFakes.MakeOverlayManager(
                toolbar: new TestFakes.FakeToolbarOverlay(),
                taskSwitcher: new TestFakes.FakeTaskSwitcherOverlay()),
        };
        var coordinator = new OverlayVmBindingCoordinator(modal, toolbar);
        SetContext(coordinator, ctx);

        switcher.IsVisible = true;
        InvokeTaskSwitcherHandler(coordinator, nameof(TaskSwitcherViewModel.IsVisible));
        Pump();
        Check.WithCustomMessage("the toolbar auto-hide must be suspended while the switcher is open")
            .That(toolbar.IsAutoHideSuspended).IsTrue();

        switcher.IsVisible = false;
        InvokeTaskSwitcherHandler(coordinator, nameof(TaskSwitcherViewModel.IsVisible));
        Pump();
        Check.WithCustomMessage("the suspension must be released when the switcher closes")
            .That(toolbar.IsAutoHideSuspended).IsFalse();
    }

    /// <summary>
    /// Fixup-02 R1: <c>ToolbarVisibilityAdapter.ForceShow</c> is POSTED, so the switcher's
    /// synchronous disarm runs first and the queued show would re-arm toolbar dwell under the open
    /// switcher. The coordinator must post its own disarm behind it. The fake models the real
    /// adapter's ordering exactly: ForceShow queues <c>SetActive(true)</c>.
    /// </summary>
    [Fact]
    public void APostedForceShowDuringTheSwitcherShow_LeavesToolbarDwellDisarmed()
    {
        var modal = new ModalInteractionState();
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var toolbarVm = new ExposedDwellToolbarViewModel(settings, messenger);
        toolbarVm.DwellDetector.AddTarget(new DwellTarget(
            "Close", new PhysicalPoint(100, 100), radius: 50, dwellTimeMs: 1000));
        toolbarVm.SetActive(false);

        var controller = new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: 1920, screenH: 1200,
            toolbarRect: new PhysicalRect(810, 0, 300, 50),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        var visibility = new PostingForceShowVisibility(toolbarVm, Dispatcher.CurrentDispatcher);
        controller.Attach(visibility);
        controller.HideForTest(visibility);      // auto-hidden just before the switcher opened

        var toolbarCoordinator = new ToolbarLifecycleCoordinator(modal);
        toolbarCoordinator.InjectAutoHideForTesting(controller);

        var switcher = new TaskSwitcherViewModel(windowManager: null!, settings, messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Messenger = messenger,
            Settings = settings,
            Dispatcher = Dispatcher.CurrentDispatcher,
            TaskSwitcherVM = switcher,
            ToolbarVM = toolbarVm,
            OverlayManager = TestFakes.MakeOverlayManager(
                toolbar: new TestFakes.FakeToolbarOverlay(),
                taskSwitcher: new TestFakes.FakeTaskSwitcherOverlay()),
        };
        var coordinator = new OverlayVmBindingCoordinator(modal, toolbarCoordinator);
        SetContext(coordinator, ctx);

        switcher.IsVisible = true;
        InvokeTaskSwitcherHandler(coordinator, nameof(TaskSwitcherViewModel.IsVisible));
        Pump();   // the fake's queued SetActive(true), then the coordinator's queued re-assert

        Check.WithCustomMessage("the test did not exercise the suspended-recover ForceShow")
            .That(visibility.ForceShowCalls).IsEqualTo(1);
        ToolbarGazeDrive.Stream(toolbarVm, 100, 100, 500_000);
        Check.WithCustomMessage(
                "a ForceShow posted during the switcher show re-armed toolbar dwell underneath it")
            .That(toolbarVm.LastProgress).IsEqualTo(0);
    }

    private static void InvokeTaskSwitcherHandler(OverlayVmBindingCoordinator coordinator, string propertyName)
    {
        var method = typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnTaskSwitcherVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(coordinator, new object?[] { null, new PropertyChangedEventArgs(propertyName) });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Models <c>ToolbarVisibilityAdapter.ForceShow</c>: the show, and with it the dwell
    /// arm, arrives on a POSTED dispatcher item, not synchronously.</summary>
    private sealed class PostingForceShowVisibility : IToolbarVisibility
    {
        private readonly ToolbarViewModel _vm;
        private readonly Dispatcher _dispatcher;

        internal PostingForceShowVisibility(ToolbarViewModel vm, Dispatcher dispatcher)
        {
            _vm = vm;
            _dispatcher = dispatcher;
        }

        public int ForceShowCalls { get; private set; }

        public void ForceShow()
        {
            ForceShowCalls++;
            _dispatcher.BeginInvoke(() => _vm.SetActive(true));
        }

        public void ShowToolbar() { }
        public void HideToolbar(bool paused) { }
        public void PositionIndicator(string edge) { }
        public void SetPauseIndicator(bool visible) { }
        public void SetActive(bool active) { }
    }

    /// <summary>
    /// The switcher-show handler passes the docked toolbar's REAL physical band to the window as
    /// the keep-out (T2), and passes nothing when the band is floating or off screen — the window
    /// then applies the screen edge margins alone, exactly like the T1 landing-guard contract says.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SwitcherShow_PassesTheKeepOut_OnlyForADockedOnScreenToolbar(bool docked, bool onScreen)
    {
        var modal = new ModalInteractionState();
        var settings = new EyeCtrlSettings();
        settings.Toolbar.IsDocked = docked;
        settings.Toolbar.DockedEdge = "Bottom";

        var messenger = new WeakReferenceMessenger();
        var switcher = new TaskSwitcherViewModel(windowManager: null!, settings, messenger);
        var toolbar = new TestFakes.FakeToolbarOverlay
        {
            OnScreen = onScreen,
            Bounds = new PhysicalRect(0, 1080, 1920, 120),
        };
        var window = new RecordingTaskSwitcherOverlay();

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Messenger = messenger,
            Settings = settings,
            Dispatcher = Dispatcher.CurrentDispatcher,
            TaskSwitcherVM = switcher,
            ToolbarVM = new ToolbarViewModel(settings, messenger),
            OverlayManager = TestFakes.MakeOverlayManager(toolbar: toolbar, taskSwitcher: window),
        };
        var coordinator = new OverlayVmBindingCoordinator(modal);
        SetContext(coordinator, ctx);

        switcher.IsVisible = true;
        InvokeTaskSwitcherHandler(coordinator, nameof(TaskSwitcherViewModel.IsVisible));
        Pump();

        Check.That(window.ShowCalls).IsEqualTo(1);
        if (docked && onScreen)
        {
            Check.That(window.LastKeepOut.HasValue).IsTrue();
            Check.That(window.LastKeepOut!.Value.Bounds)
                .IsEqualTo(new PhysicalRect(0, 1080, 1920, 120));
            Check.That(window.LastKeepOut.Value.DockedEdge).IsEqualTo("Bottom");
        }
        else
        {
            Check.WithCustomMessage(
                    "a floating or off-screen band must not claim a keep-out: its stale bounds "
                    + "would push the tiles off the space the user can actually see")
                .That(window.LastKeepOut.HasValue).IsFalse();
        }
    }

    /// <summary>
    /// Records ring Show/Hide so the probe gate is observable without a window.
    /// </summary>
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

    /// <summary>Records the show call and its keep-out argument without a fullscreen window.</summary>
    private sealed class RecordingTaskSwitcherOverlay : ITaskSwitcherOverlay
    {
        public int ShowCalls { get; private set; }
        public ToolbarKeepOut? LastKeepOut { get; private set; }

        public void ShowFullScreen(ToolbarKeepOut? toolbarKeepOut)
        {
            ShowCalls++;
            LastKeepOut = toolbarKeepOut;
        }

        public void HideOverlay() { }
    }
}
