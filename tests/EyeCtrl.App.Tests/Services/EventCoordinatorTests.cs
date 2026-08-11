namespace EyeCtrl.App.Tests.Services;

using System;
using System.Reflection;
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
        var input = new TestFakes.FakeInputSimulator();
        SetContext(coordinator, TestFakes.MakeContext(input));

        ExecuteKeyboardShortcut(coordinator, "Ctrl+!");

        Check.That(input.LastModifiedKeyPress).IsNotNull();
        Check.That(input.LastModifiedKeyPress!.VirtualKeyCode).IsEqualTo((ushort)0x31);
        Check.That(input.LastModifiedKeyPress!.Modifiers).Contains(ModifierKey.Ctrl);
        Check.That(input.LastModifiedKeyPress!.Modifiers).Contains(ModifierKey.Shift);
    }

    [Fact]
    public void ExecuteMediaControl_EveryPreset_ProducesAnExtendedKeyPress()
    {
        var failures = new List<string>();
        foreach (var (label, command) in EyeCtrl.Core.Models.QuickActionPresets.MediaControls)
        {
            var coordinator = new EventCoordinator();
            var input = new TestFakes.FakeInputSimulator();
            SetContext(coordinator, TestFakes.MakeContext(input));

            typeof(EventCoordinator)
                .GetMethod("ExecuteMediaControl", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(coordinator, new object[] { command });

            if (input.LastKeyPress is not { } kp || kp.Vk == 0 || !kp.Extended)
                failures.Add($"{label} ({command})");
        }

        Check.That(failures).IsEmpty();
    }

    private static void SetContext(EventCoordinator coordinator, EventCoordinationContext context)
    {
        typeof(EventCoordinator)
            .GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, context);
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
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, TestFakes.MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsEqualTo(expected);
    }

    [Fact]
    public void ExecuteWindowAction_NullHwnd_NoOps()
    {
        // Fail-closed: the caller resolved no eligible foreground window. EligibleHwnd is set even
        // though the method never reads it — the null ARGUMENT must win regardless.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, TestFakes.MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", null);

        Check.That(fake.LastOp).IsNull();
    }

    [Theory]
    [InlineData("NotAWindowAction")]  // Enum.TryParse fails outright
    [InlineData("7")]                 // Enum.TryParse SUCCEEDS — undefined member; see the caveat
    public void ExecuteWindowAction_RejectedPayload_NoOps(string payload)
    {
        // A corrupt or hand-edited quickactions.json must not fire an arbitrary op. The two cases
        // reach the guard differently: Enum.TryParse also accepts NUMERIC strings, so "7" parses
        // true into an undefined WindowAction, and only the Enum.IsDefined half rejects it.
        //
        // CAVEAT — this does NOT pin the Enum.IsDefined clause, despite appearances. Verified by
        // mutation: deleting `|| !Enum.IsDefined(windowAction)` leaves BOTH cases green, because an
        // undefined value falls through the arm-less switch and fires nothing either way.
        // IsDefined's only observable effect is the Log.Warning, and nothing in this suite captures
        // Serilog output. What the "7" case does guard is that no op fires for a numeric payload —
        // it would catch a regression adding a `default:` arm that did real work, or a switch arm
        // for an undefined value. Treat IsDefined itself as review-verified, not test-verified.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, TestFakes.MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_DoesNotQueryForegroundItself()
    {
        // The HWND is resolved by the message recipient on the dwell tick and passed in, so the
        // executor must never re-query — a re-query after the dispatcher hop is exactly the
        // foreground-change gap this design closes.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var coord = new EventCoordinator();
        SetContext(coord, TestFakes.MakeContextWithWindowManager(fake));

        InvokeExecuteWindowAction(coord, "Close", new IntPtr(0x1234));

        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }

    [Fact]
    public void ExecuteQuickAction_WindowAction_ForwardsTheResolvedHwnd()
    {
        // EligibleHwnd = null proves the passed argument wins — if the arm ever re-resolved
        // instead of forwarding, LastOp would stay null.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = null };
        var coord = new EventCoordinator();
        SetContext(coord, TestFakes.MakeContextWithWindowManager(fake));

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
}
