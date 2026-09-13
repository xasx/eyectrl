namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The real entry chain (review PR #139 round 3 finding 1 — its repro, inverted into a
/// regression test): live gaze stream → real GazeEngine → ModesMenu dwell row →
/// EnterRepeatModeMessage → mode active. Before the fixation-suppression fix the row's 700 ms
/// dwell raced the engine's 300 ms fixation, the state at activation was <c>Fixation</c>, and
/// the FreeLook-guarded entry silently no-oped — the mode could never start through its own UI.
/// </summary>
public class RepeatModeEntryPathTests
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
    public void ModesMenuDwell_EntersRepeatMode_EvenWhileGazeStreams()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var provider = new StubProvider();
        var engine = new EyeCtrl.Core.GazeEngine(provider, new SilentInput(), settings);
        var im = engine.InteractionManager;
        var menuVm = new ModesMenuViewModel(settings, messenger);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(new TestFakes.FakeEdgeButtonBar()),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            GazeEngine = engine,
            ModesMenuVM = menuVm,
        };
        using var controller = new RepeatModeController();
        controller.Initialize(ctx);

        // The adapter's cursor-forwarding role, replicated: engine → messenger. Main publishes
        // a GazeFrame (gap flag); the dwell surfaces skip gap-held samples downstream.
        engine.CursorMoved += frame => dispatcher.BeginInvoke(() =>
            messenger.Send(new GazeCursorMovedMessage(frame.Position, frame.TimestampMicroseconds)));

        // Open the menu (the toolbar button's message) and place the row target.
        messenger.Send(new ModesMenuToggledMessage());
        menuVm.SetRowScreenPositions(new[] { ("modes_left", new PhysicalPoint(500, 500), 40.0) });
        Pump(dispatcher);
        Check.That(menuVm.IsVisible).IsTrue();

        var sawFixation = false;
        im.StateChanged += s => { if (s == InteractionState.Fixation) sawFixation = true; };

        // Live gaze: hold over the row for well past fixation (300) and dwell (700).
        long clockUs = 0;
        for (var ms = 0; ms < 1200; ms += 33)
        {
            clockUs += 33_000;
            provider.Push(new GazePoint(500, 500, clockUs, true));
            Pump(dispatcher);
        }

        Pump(dispatcher);
        Check.WithCustomMessage(
                "The menu suppresses engine fixation while visible; a fixation here would block " +
                "the FreeLook-guarded entry (round 3 finding 1).")
            .That(sawFixation).IsFalse();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(im.RepeatAction).IsEqualTo(InteractionAction.LeftClick);

        engine.Dispose();
    }

    private static void Pump(System.Windows.Threading.Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
}
