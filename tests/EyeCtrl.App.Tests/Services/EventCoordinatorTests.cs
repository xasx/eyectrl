namespace EyeCtrl.App.Tests.Services;

using System;
using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Tests for EventCoordinator.
/// Note: EventCoordinator is tightly coupled to WPF windows and ViewModels, making comprehensive
/// unit testing challenging without extensive mocking infrastructure. These tests focus on
/// basic contract verification. Full behavioral testing is performed through integration tests.
/// </summary>
public class EventCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert
        Check.ThatCode(() => coordinator.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void WordPredictor_ReturnsNull_BeforeInitialization()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act
        var predictor = coordinator.WordPredictor;

        // Assert
        Check.That(predictor).IsNull();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert - should not throw on multiple disposals
        coordinator.Dispose();
        coordinator.Dispose();
        coordinator.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert - should not throw when disposed without initialization
        coordinator.Dispose();
    }

    [Fact]
    public void LoadKeyboard_ThrowsArgumentNullException_WhenKeyboardIsNull()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert
        Check.ThatCode(() =>
            coordinator.LoadKeyboard(null!, null!, null!, null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void ExecuteKeyboardShortcut_CharacterRequiringShift_MergesRequiredModifier()
    {
        var coordinator = new EventCoordinator();
        var input = new FakeInputSimulator();
        SetContext(coordinator, MakeContext(input));

        ExecuteKeyboardShortcut(coordinator, "Ctrl+!");

        Check.That(input.LastModifiedKeyPress).IsNotNull();
        Check.That(input.LastModifiedKeyPress!.VirtualKeyCode).IsEqualTo((ushort)0x31);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Ctrl);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Shift);
    }

    [Fact]
    public void ExecuteMediaControl_EveryPreset_ProducesAnExtendedKeyPress()
    {
        var failures = new List<string>();
        foreach (var (label, command) in EyeCtrl.Core.Models.QuickActionPresets.MediaControls)
        {
            var coordinator = new EventCoordinator();
            var input = new FakeInputSimulator();
            SetContext(coordinator, MakeContext(input));

            typeof(EventCoordinator)
                .GetMethod("ExecuteMediaControl", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(coordinator, new object[] { command });

            if (input.LastKeyPress is not { } kp || kp.Vk == 0 || !kp.Extended)
                failures.Add($"{label} ({command})");
        }

        Check.That(failures).IsEmpty();
    }

    [Fact]
    public void QuickActionsEnabledSetting_SuspendsAndResumesSummonController()
    {
        var settings = new EyeCtrlSettings();
        settings.QuickActions.Enabled = false;
        var coordinator = new EventCoordinator();
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        controller.SetSuspended(true);

        SetContext(coordinator, new EventCoordinationContext(
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
            Messenger: null!,
            Dispatcher: null!,
            SettingsService: null!,
            Settings: settings,
            TargetHighlightVM: null!,
            InputSimulator: null!,
            CalibrationService: null!,
            NoteManager: null!,
            TrackStatusVM: null!,
            TaskSwitcherVM: null!,
            WindowManager: null!,
            QuickActionsGridVM: null!,
            QuickActionsStore: null!,
            WindowsAppLauncher: null!,
            InstalledAppProvider: new FakeInstalledAppProvider()));
        SetField(coordinator, "_quickActionsSummon", controller);

        InvokeSetupQuickActionsSummon(coordinator);
        Check.That(GetQuickActionsSuspended(controller)).IsTrue();

        settings.QuickActions.Enabled = true;
        InvokeQuickActionsSettingsHandler(coordinator, nameof(QuickActionsSettings.Enabled));
        Check.That(GetQuickActionsSuspended(controller)).IsFalse();
    }

    private static EventCoordinationContext MakeContext(IInputSimulator input)
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
            Messenger: null!,
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
    /// (mirrors how <see cref="MakeContext"/> overrides the simulator). All other dependencies stay
    /// null! — only enough to exercise <c>ExecuteWindowAction</c>.
    /// </summary>
    private static EventCoordinationContext MakeContextWithWindowManager(IWindowManager windowManager)
    {
        var ctx = MakeContext(new FakeInputSimulator());
        return ctx with { WindowManager = windowManager };
    }

    private static void SetContext(EventCoordinator coordinator, EventCoordinationContext context)
    {
        typeof(EventCoordinator)
            .GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, context);
    }

    private static void SetField(EventCoordinator coordinator, string fieldName, object? value)
    {
        typeof(EventCoordinator)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, value);
    }

    private static void InvokeSetupQuickActionsSummon(EventCoordinator coordinator)
    {
        typeof(EventCoordinator)
            .GetMethod("SetupQuickActionsSummon", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, null);
    }

    private static void InvokeQuickActionsSettingsHandler(EventCoordinator coordinator, string propertyName)
    {
        var handler = (PropertyChangedEventHandler?)typeof(EventCoordinator)
            .GetField("_quickActionsSettingsHandler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator);
        handler?.Invoke(coordinator, new PropertyChangedEventArgs(propertyName));
    }

    private static bool GetQuickActionsSuspended(QuickActionsSummonController controller)
    {
        return (bool)typeof(QuickActionsSummonController)
            .GetField("_suspended", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!;
    }

    private static void ExecuteKeyboardShortcut(EventCoordinator coordinator, string chord)
    {
        typeof(EventCoordinator)
            .GetMethod("ExecuteKeyboardShortcut", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, new object[] { chord });
    }

    private static void InvokeExecuteWindowAction(EventCoordinator coordinator, string payload, IntPtr? hwnd)
    {
        typeof(EventCoordinator)
            .GetMethod("ExecuteWindowAction", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, new object?[] { payload, hwnd });
    }

    [Theory]
    [InlineData("Minimize", WindowAction.Minimize)]
    [InlineData("MaximizeRestore", WindowAction.MaximizeRestore)]
    [InlineData("Close", WindowAction.Close)]
    public void ExecuteWindowAction_RoutesPayloadToTheRightOp(string payload, WindowAction expected)
    {
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsEqualTo(expected);
    }

    [Fact]
    public void ExecuteWindowAction_NullHwnd_NoOps()
    {
        // Fail-closed: the caller resolved no eligible foreground window. EligibleHwnd is set even
        // though the method never reads it — the null ARGUMENT must win regardless.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", null);

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_UnparseablePayload_NoOps()
    {
        // A corrupt quickactions.json must not fire an arbitrary op.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "NotAWindowAction", new IntPtr(0x1234));

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_DoesNotQueryForegroundItself()
    {
        // The HWND is resolved by the message recipient on the dwell tick and passed in, so the
        // executor must never re-query — a re-query after the dispatcher hop is exactly the
        // foreground-change gap this design closes.
        var fake = new FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", new IntPtr(0x1234));

        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }

    [Fact]
    public void ExecuteQuickAction_WindowAction_ForwardsTheResolvedHwnd()
    {
        // EligibleHwnd = null proves the passed argument wins — if the arm ever re-resolved
        // instead of forwarding, LastOp would stay null.
        var fake = new FakeWindowActionManager { EligibleHwnd = null };
        var coord = new EventCoordinator();
        SetContext(coord, MakeContextWithWindowManager(fake));

        typeof(EventCoordinator)
            .GetMethod("ExecuteQuickAction", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coord, new object?[]
            {
                new QuickAction { Type = QuickActionType.WindowAction, Label = "test", Payload = "Minimize" },
                new IntPtr(0x1234)
            });

        Check.That(fake.LastOp).IsEqualTo(WindowAction.Minimize);
        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public ModifiedKeyPress? LastModifiedKeyPress { get; private set; }

        public (ushort Vk, bool Extended)? LastKeyPress { get; private set; }

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

        public void MouseDown(PhysicalPoint point)
        {
        }

        public void MouseUp(PhysicalPoint point)
        {
        }

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

    private sealed record ModifiedKeyPress(IReadOnlySet<ModifierKey> Modifiers, ushort VirtualKeyCode, bool IsExtendedKey);

    private sealed class FakeInstalledAppProvider : IInstalledAppProvider
    {
        public IReadOnlyList<InstalledApp> GetInstalledApps() => Array.Empty<InstalledApp>();
    }

    /// <summary>
    /// Fake <see cref="IWindowManager"/> that records the last op fired + how many times the
    /// eligibility query ran. EligibilityQueries is the proxy for "no deferred re-query" — a
    /// synchronous handler queries exactly once; a queued hop would (in production) re-query at
    /// pump time and (in a unit test) never pump at all.
    /// </summary>
    private sealed class FakeWindowActionManager : IWindowManager
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
