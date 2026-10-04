namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
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

    [StaFact]
    public void ModesMenuOpenAndClose_WhileKeyboardHoldsSuppression_LeavesFixationSuppressed()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var engine = new EyeCtrl.Core.GazeEngine(new StubProvider(), new SilentInput(), settings);
        var menuVm = new ModesMenuViewModel(settings, messenger);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(new TestFakes.FakeEdgeButtonBar()),
            InteractionManager = engine.InteractionManager,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            GazeEngine = engine,
            ModesMenuVM = menuVm,
        };
        using var controller = new RepeatModeController();
        controller.Initialize(ctx);

        // The keyboard is up: it holds its own suppression claim (KeyboardCoordinator does this
        // on a toolbar show).
        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);

        messenger.Send(new ModesMenuToggledMessage());   // open
        Pump(dispatcher);
        Check.That(menuVm.IsVisible).IsTrue();

        messenger.Send(new ModesMenuToggledMessage());   // close
        Pump(dispatcher);
        Check.That(menuVm.IsVisible).IsFalse();

        Check.WithCustomMessage(
                "Closing the Modes menu released the keyboard's suppression: offers would pop " +
                "over the text being typed.")
            .That(engine.SuppressFixation).IsTrue();

        engine.Dispose();
    }

    [StaFact]
    public void ModesMenuOpenAndClose_OnItsOwn_LiftsSuppression()
    {
        // The menu must release the claim it took: a release under another owner's key leaks the
        // menu's claim and leaves fixation suppressed for good after the menu closes.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var engine = new EyeCtrl.Core.GazeEngine(new StubProvider(), new SilentInput(), settings);
        var menuVm = new ModesMenuViewModel(settings, messenger);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(new TestFakes.FakeEdgeButtonBar()),
            InteractionManager = engine.InteractionManager,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            GazeEngine = engine,
            ModesMenuVM = menuVm,
        };
        using var controller = new RepeatModeController();
        controller.Initialize(ctx);

        messenger.Send(new ModesMenuToggledMessage());   // open
        Pump(dispatcher);
        Check.That(engine.SuppressFixation).IsTrue();

        messenger.Send(new ModesMenuToggledMessage());   // close
        Pump(dispatcher);
        Check.WithCustomMessage("Closing the Modes menu left fixation suppressed.")
            .That(engine.SuppressFixation).IsFalse();

        engine.Dispose();
    }

    private static void Pump(System.Windows.Threading.Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
}
