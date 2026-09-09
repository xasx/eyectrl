namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Shared fakes and context builders for coordinator tests. Extracted from <c>EventCoordinatorTests</c>
/// so the decomposed sub-coordinator tests (Drag/QuickActions/...) can reuse the same fakes without
/// each test class re-declaring them.
/// </summary>
internal static class TestFakes
{
    /// <summary>
    /// Builds a context wired only enough to exercise input-simulator paths. <c>Messenger</c> is a
    /// real <see cref="WeakReferenceMessenger"/> because coordinators <c>Register</c> on it during
    /// <c>Initialize</c> and NRE on null.
    /// </summary>
    internal static EventCoordinationContext MakeContext(IInputSimulator input)
    {
        return new EventCoordinationContext(
            CursorVM: null!,
            ActivationVM: null!,
            ManualSelectionVM: null!,
            ManualSelectionIndicatorVM: null!,
            ActionMenuVM: null!,
            ToolbarVM: null!,
            KeyboardVM: null!,
            BackdropVM: null!,
            DragTrailVM: null!,
            OverlayManager: null!,
            GazeEngine: null!,
            InteractionManager: null!,
            Messenger: new WeakReferenceMessenger(),
            Dispatcher: null!,
            SettingsService: null!,
            Settings: null!,
            TargetHighlightVM: null!,
            InputSimulator: input,
            CalibrationService: null!,
            NoteManager: null!,
            TrackStatusVM: null!,
            TaskSwitcherVM: null!,
            WindowManager: null!,
            QuickActionsGridVM: null!,
            QuickActionsStore: null!,
            WindowsAppLauncher: null!,
            InstalledAppProvider: new FakeInstalledAppProvider());
    }

    /// <summary>
    /// Builds a context whose <see cref="EventCoordinationContext.WindowManager"/> is the given fake
    /// (mirrors how <see cref="MakeContext(IInputSimulator)"/> overrides the simulator). All other
    /// dependencies stay null! — only enough to exercise <c>ExecuteWindowAction</c>.
    /// </summary>
    internal static EventCoordinationContext MakeContextWithWindowManager(IWindowManager windowManager)
    {
        var ctx = MakeContext(new FakeInputSimulator());
        return ctx with { WindowManager = windowManager };
    }

    /// <summary>
    /// Builds a context wired enough for <see cref="Coordinators.DragCoordinator"/> to
    /// <c>Initialize</c>: its <c>OnInitialize</c> eagerly dereferences <c>InteractionManager</c>
    /// and <c>DragTrailVM</c>, which the default builder leaves <c>null!</c>. Both are real
    /// objects — neither has an App-layer dependency, and no mocking library is available.
    /// <c>OverlayManager</c> stays null: reaching it needs a real WPF <c>EdgeButtonBarWindow</c>,
    /// because <c>IOverlayManager.EdgeButtonBar</c> is typed as the concrete window rather than an
    /// interface. Drag teardown treats that dereference as a failure and carries on, which is why
    /// the button-release assertions still hold here.
    /// </summary>
    internal static EventCoordinationContext MakeDragContext(IInputSimulator input)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        return MakeContext(input) with
        {
            Messenger = messenger,
            Settings = settings,
            InteractionManager = new InteractionManager(),
            DragTrailVM = new DragTrailViewModel(settings, messenger),
        };
    }

    /// <summary>
    /// Builds a context carrying the given settings — for tests that drive settings-driven setup
    /// (e.g. <see cref="Coordinators.QuickActionsCoordinator"/> suspend/resume).
    /// </summary>
    internal static EventCoordinationContext MakeContext(EyeCtrlSettings settings)
    {
        var ctx = MakeContext(new FakeInputSimulator());
        return ctx with { Settings = settings };
    }

    internal sealed class FakeInputSimulator : IInputSimulator
    {
        public ModifiedKeyPress? LastModifiedKeyPress { get; private set; }

        public (ushort Vk, bool Extended)? LastKeyPress { get; private set; }

        public PhysicalPoint? LastMiddleClick { get; private set; }

        /// <summary>
        /// Every button transition this fake was asked to perform, in order.
        /// <para>
        /// These were empty bodies. The recording fakes lived in <c>Core.Tests</c> — wired to
        /// <c>GazeEngine</c>, which only ever moves the cursor — while the layer that actually
        /// presses and releases the button discarded the calls. So the apparatus recorded injected
        /// input where none is injected and threw it away where it is, and <c>MouseUp</c> ended up
        /// asserted in none of the suite's ~2,900 tests.
        /// </para>
        /// <para>
        /// Only <c>MouseDown</c>/<c>MouseUp</c> are recorded here. The click methods —
        /// <c>LeftClick</c>, <c>MiddleClick</c> and friends — are single interface calls that press
        /// and release atomically, so they cannot leave a button held and do not belong in a ledger
        /// whose purpose is detecting one that is.
        /// </para>
        /// </summary>
        public List<(string Action, PhysicalPoint Point)> ButtonEvents { get; } = new();

        public int MouseDownCount => ButtonEvents.Count(e => e.Action == "MouseDown");

        public int MouseUpCount => ButtonEvents.Count(e => e.Action == "MouseUp");

        /// <summary>
        /// The invariant that matters: the OS button is not left pressed. Asserted as a count
        /// identity rather than per-path equality so it holds for exit paths nobody enumerated —
        /// which is how an unreleased button got in.
        /// </summary>
        public bool ButtonsBalanced => MouseDownCount == MouseUpCount;

        public void MoveCursor(PhysicalPoint point)
        {
        }

        public void LeftClick(PhysicalPoint point)
        {
        }

        public void RightClick(PhysicalPoint point)
        {
        }

        public void DoubleClick(PhysicalPoint point)
        {
        }

        public void MiddleClick(PhysicalPoint point)
        {
            LastMiddleClick = point;
        }

        public void MouseDown(PhysicalPoint point) => ButtonEvents.Add(("MouseDown", point));

        public void MouseUp(PhysicalPoint point) => ButtonEvents.Add(("MouseUp", point));

        public void Scroll(PhysicalPoint point, int deltaX, int deltaY)
        {
        }

        public void SendKeyPress(ushort virtualKeyCode)
        {
        }

        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)
        {
            LastKeyPress = (virtualKeyCode, isExtendedKey);
        }

        public void SendKeyDown(ushort virtualKeyCode)
        {
        }

        public void SendKeyUp(ushort virtualKeyCode)
        {
        }

        public void SendText(string text)
        {
        }

        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)
        {
            LastModifiedKeyPress = new ModifiedKeyPress(modifiers, virtualKeyCode, IsExtendedKey: false);
        }

        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
        {
            LastModifiedKeyPress = new ModifiedKeyPress(modifiers, virtualKeyCode, isExtendedKey);
        }
    }

    internal sealed record ModifiedKeyPress(IReadOnlySet<ModifierKey> Modifiers, ushort VirtualKeyCode, bool IsExtendedKey);

    internal sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }

    /// <summary>
    /// Superset of the three former private IGazeProvider fakes: settable <c>IsConnected</c> (defaults
    /// true, per the coordinator tests' connected-state precondition) plus a raisable GazePoint event.
    /// Not the Core test project's fake — that one's IsConnected has a private set flipped by Start().
    /// </summary>
    public class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; set; } = true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Raise(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    /// <summary>
    /// Fake <see cref="IWindowManager"/> that records the last op fired + how many times the
    /// eligibility query ran. EligibilityQueries is the proxy for "no deferred re-query" — a
    /// synchronous handler queries exactly once; a queued hop would (in production) re-query at
    /// pump time and (in a unit test) never pump at all.
    /// </summary>
    internal sealed class FakeWindowActionManager : IWindowManager
    {
        public IntPtr? EligibleHwnd { get; set; }
        public WindowAction? LastOp { get; private set; }
        public int EligibilityQueries { get; private set; }
        public bool ClosePosted { get; set; } = true;

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => Array.Empty<AppWindow>();
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow()
        {
            EligibilityQueries++;
            return EligibleHwnd;
        }
        public void Minimize(IntPtr hwnd) => LastOp = WindowAction.Minimize;
        public void MaximizeOrRestore(IntPtr hwnd) => LastOp = WindowAction.MaximizeRestore;
        public bool CloseWindow(IntPtr hwnd) { LastOp = WindowAction.Close; return ClosePosted; }
    }
}
