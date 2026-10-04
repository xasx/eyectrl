namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Fixation suppression has several owners (keyboard, calibration, Modes menu, external dwell);
/// one owner's release must not cancel another's hold. Before the claim set, the Modes menu wrote
/// the shared flag false on close, so a user who opened and closed it while the keyboard was up
/// got fixation onsets and activation offers back over the text they were typing
/// (architecture-check-2026-10, before-adoption item 1).
/// <para>
/// The claim set brings a new failure: a call site using the wrong key, or skipping its release,
/// leaves fixation suppressed until restart — no other owner's release lifts it. So each owner's
/// pairing is pinned here through its real message handler, with a second owner's claim held in
/// parallel. The keyboard's pairing lives in <see cref="KeyboardCoordinatorTests"/> (its handler
/// needs the real window).
/// </para>
/// </summary>
public class FixationSuppressionOwnershipTests
{
    private sealed class StubProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void Push(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private sealed class SilentInput : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) { }
        public void LeftClick(PhysicalPoint p) { }
        public void RightClick(PhysicalPoint p) { }
        public void DoubleClick(PhysicalPoint p) { }
        public void MouseDown(PhysicalPoint p) { }
        public void MouseUp(PhysicalPoint p) { }
        public void Scroll(PhysicalPoint p, int dx, int dy) { }
        public void MiddleClick(PhysicalPoint p) { }
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> m, ushort vk) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> m, ushort vk, bool ext) { }
    }

    /// <summary>A real engine plus the context pieces the Modes-menu and calibration handlers use.</summary>
    private sealed class World : IDisposable
    {
        public System.Windows.Threading.Dispatcher Dispatcher { get; } =
            System.Windows.Threading.Dispatcher.CurrentDispatcher;
        public WeakReferenceMessenger Messenger { get; } = new();
        public EyeCtrlSettings Settings { get; } = new();
        public GazeEngine Engine { get; }
        public ModesMenuViewModel MenuVm { get; }
        public EventCoordinationContext Context { get; }

        public World()
        {
            Engine = new GazeEngine(new StubProvider(), new SilentInput(), Settings);
            MenuVm = new ModesMenuViewModel(Settings, Messenger);
            Context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                OverlayManager = TestFakes.MakeOverlayManager(
                    new TestFakes.FakeEdgeButtonBar(),
                    manualSelection: new TestFakes.FakeManualSelectionOverlay(),
                    manualSelectionIndicator: new TestFakes.FakeManualSelectionIndicatorOverlay(),
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    activationButton: new TestFakes.FakeActivationButtonOverlay(),
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay(),
                    actionMenu: new TestFakes.FakeActionMenuOverlay(),
                    calibration: new TestFakes.FakeCalibrationOverlay()),
                InteractionManager = Engine.InteractionManager,
                Messenger = Messenger,
                Dispatcher = Dispatcher,
                Settings = Settings,
                GazeEngine = Engine,
                ModesMenuVM = MenuVm,
                CursorVM = new CursorCircleViewModel(Settings, Messenger),
            };
        }

        public void Pump() =>
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        public void Send<T>(T message) where T : class
        {
            Messenger.Send(message);
            Pump();
        }

        public void Dispose() => Engine.Dispose();
    }

    private static OverlayMessageCoordinator NewCalibrationOwner(World w)
    {
        var modal = new ModalInteractionState();
        var c = new OverlayMessageCoordinator(
            new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
        c.Initialize(w.Context);
        return c;
    }

    // ── Modes menu ────────────────────────────────────────────────────────────

    [StaFact]
    public void ModesMenuOpenAndClose_WhileKeyboardHoldsSuppression_LeavesFixationSuppressed()
    {
        using var w = new World();
        using var controller = new RepeatModeController();
        controller.Initialize(w.Context);

        // The keyboard is up: it holds its own suppression claim (KeyboardCoordinator does this
        // on a toolbar show).
        w.Engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);

        w.Send(new ModesMenuToggledMessage());   // open
        Check.That(w.MenuVm.IsVisible).IsTrue();

        w.Send(new ModesMenuToggledMessage());   // close
        Check.That(w.MenuVm.IsVisible).IsFalse();

        Check.WithCustomMessage(
                "Closing the Modes menu released the keyboard's suppression: offers would pop " +
                "over the text being typed.")
            .That(w.Engine.SuppressFixation).IsTrue();
    }

    [StaFact]
    public void ModesMenuOpenAndClose_OnItsOwn_LiftsSuppression()
    {
        // The menu must release the claim it took: a release under another owner's key leaks the
        // menu's claim and leaves fixation suppressed for good after the menu closes.
        using var w = new World();
        using var controller = new RepeatModeController();
        controller.Initialize(w.Context);

        w.Send(new ModesMenuToggledMessage());   // open
        Check.That(w.Engine.SuppressFixation).IsTrue();

        w.Send(new ModesMenuToggledMessage());   // close
        Check.WithCustomMessage("Closing the Modes menu left fixation suppressed.")
            .That(w.Engine.SuppressFixation).IsFalse();
    }

    [StaFact]
    public void ModesMenuOpen_EndsAFixationInFlight()
    {
        // Once suppressed the detector raises no end, so the menu ends the run itself; a state left
        // at Fixation would make the FreeLook-guarded EnterRepeatMode no-op on the row dwell (the
        // PR #139 round-3 bug). Mutation: drop the OnFixationEnded() after the menu's acquire → red.
        using var w = new World();
        using var controller = new RepeatModeController();
        controller.Initialize(w.Context);

        w.Engine.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        Check.That(w.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);

        w.Send(new ModesMenuToggledMessage());   // open
        Check.That(w.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(CalibrationState.Active)]
    [InlineData(CalibrationState.Failed)]
    [InlineData(CalibrationState.Idle)]
    [InlineData(CalibrationState.Cancelled)]
    public void CalibrationClaim_IsTakenWhileCollecting_AndReleasedOnEveryEnd(CalibrationState end)
    {
        // Mutations: the Active/Failed or the Idle/Cancelled branch skips its release, or releases
        // under another key → calibration's claim leaks and suppression stays on → red.
        using var w = new World();
        using var c = NewCalibrationOwner(w);

        w.Send(new CalibrationStateChangedMessage(CalibrationState.Collecting, 0));
        Check.That(w.Engine.SuppressFixation).IsTrue();
        w.Send(new CalibrationStateChangedMessage(CalibrationState.Training, 100));
        Check.That(w.Engine.SuppressFixation).IsTrue();

        w.Send(new CalibrationStateChangedMessage(end, 100));
        Check.WithCustomMessage($"Calibration ended ({end}) but left fixation suppressed.")
            .That(w.Engine.SuppressFixation).IsFalse();
    }

    [StaFact]
    public void CalibrationClaim_UsesItsOwnKey()
    {
        // With the Modes menu holding a claim in parallel, calibration's end must leave that claim
        // alone and must not leak its own. Mutation: calibration acquires under another owner's key
        // → its release misses, that key leaks, and suppression survives the menu's release → red.
        using var w = new World();
        using var c = NewCalibrationOwner(w);

        w.Engine.AcquireFixationSuppression(FixationSuppressor.ModesMenu);
        w.Send(new CalibrationStateChangedMessage(CalibrationState.Collecting, 0));
        w.Send(new CalibrationStateChangedMessage(CalibrationState.Cancelled, 0));
        Check.WithCustomMessage("Calibration's end released the Modes menu's claim.")
            .That(w.Engine.SuppressFixation).IsTrue();

        w.Engine.ReleaseFixationSuppression(FixationSuppressor.ModesMenu);
        Check.WithCustomMessage("A claim leaked: suppression outlived both owners.")
            .That(w.Engine.SuppressFixation).IsFalse();
    }
}
