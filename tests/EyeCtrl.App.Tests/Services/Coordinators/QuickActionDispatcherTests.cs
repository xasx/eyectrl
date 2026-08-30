namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// Tests for <see cref="QuickActionDispatcher"/>. Migrated from <c>InputActionDispatcherTests</c>
/// as part of the T9 split — the methods keep their names/signatures and are still
/// <c>internal</c> on the dispatcher, so the tests call them directly. Every
/// <c>Check.That(...)</c> assertion is preserved verbatim from the originals; only the
/// arrange/act plumbing changed (the constructor name).
/// </summary>
public class QuickActionDispatcherTests
{
    // ── CoordinatorBase lifecycle contract ──
    // The other eight coordinators pin this; QuickActionDispatcher was the one corner left
    // uncovered. It has no OnDispose override today, so the base's `Context?.Messenger
    // .UnregisterAll(this)` carries it — but the sibling coordinators reach Context through
    // `Context!` in their OnDispose, and this is the test that catches it if this one ever
    // grows the same shape.

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new QuickActionDispatcher();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on Context).
        new QuickActionDispatcher().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new QuickActionDispatcher();
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void ExecuteKeyboardShortcut_CharacterRequiringShift_MergesRequiredModifier()
    {
        var input = new TestFakes.FakeInputSimulator();
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContext(input));
        d.ExecuteKeyboardShortcut("Ctrl+!");   // internal, directly callable
        Check.That(input.LastModifiedKeyPress).IsNotNull();
        Check.That(input.LastModifiedKeyPress!.VirtualKeyCode).IsEqualTo((ushort)0x31);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Shift);
        Check.That(input.LastModifiedKeyPress.Modifiers).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void ExecuteMediaControl_EveryPreset_ProducesAnExtendedKeyPress()
    {
        var failures = new List<string>();
        foreach (var (label, command) in EyeCtrl.Core.Models.QuickActionPresets.MediaControls)
        {
            var input = new TestFakes.FakeInputSimulator();
            var d = new QuickActionDispatcher();
            d.Initialize(TestFakes.MakeContext(input));

            d.ExecuteMediaControl(command);

            if (input.LastKeyPress is not { } kp || kp.Vk == 0 || !kp.Extended)
                failures.Add($"{label} ({command})");
        }

        Check.That(failures).IsEmpty();
    }

    [Theory]
    [InlineData("Minimize", WindowAction.Minimize)]
    [InlineData("MaximizeRestore", WindowAction.MaximizeRestore)]
    [InlineData("Close", WindowAction.Close)]
    public void ExecuteWindowAction_RoutesPayloadToTheRightOp(string payload, WindowAction expected)
    {
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContextWithWindowManager(fake));

        d.ExecuteWindowAction(payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsEqualTo(expected);
    }

    [Fact]
    public void ExecuteWindowAction_NullHwnd_NoOps()
    {
        // Fail-closed: the caller resolved no eligible foreground window. EligibleHwnd is set even
        // though the method never reads it — the null ARGUMENT must win regardless.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContextWithWindowManager(fake));

        d.ExecuteWindowAction("Close", null);

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
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContextWithWindowManager(fake));

        d.ExecuteWindowAction(payload, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsNull();
    }

    [Fact]
    public void ExecuteWindowAction_DoesNotQueryForegroundItself()
    {
        // The HWND is resolved by the message recipient on the dwell tick and passed in, so the
        // executor must never re-query — a re-query after the dispatcher hop is exactly the
        // foreground-change gap this design closes.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = new IntPtr(0x1234) };
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContextWithWindowManager(fake));

        d.ExecuteWindowAction("Close", new IntPtr(0x1234));

        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }

    [Fact]
    public void ExecuteQuickAction_WindowAction_ForwardsTheResolvedHwnd()
    {
        // EligibleHwnd = null proves the passed argument wins — if the arm ever re-resolved
        // instead of forwarding, LastOp would stay null.
        var fake = new TestFakes.FakeWindowActionManager { EligibleHwnd = null };
        var d = new QuickActionDispatcher();
        d.Initialize(TestFakes.MakeContextWithWindowManager(fake));

        d.ExecuteQuickAction(new QuickAction { Type = QuickActionType.WindowAction, Label = "test", Payload = "Minimize" }, new IntPtr(0x1234));

        Check.That(fake.LastOp).IsEqualTo(WindowAction.Minimize);
        Check.That(fake.EligibilityQueries).IsEqualTo(0);
    }
}
