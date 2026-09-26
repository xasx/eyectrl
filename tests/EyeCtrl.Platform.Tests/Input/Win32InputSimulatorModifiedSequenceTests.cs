namespace EyeCtrl.Platform.Tests.Input;

using System.Linq;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Platform.Input;
using NFluent;
using Xunit;

public class Win32InputSimulatorModifiedSequenceTests
{
    private const ushort MenuMask = Win32InputSimulator.MenuMaskVirtualKey;

    // The body builders are instance methods (they route through CreateMouseInput's coordinate
    // mapper), so the tests construct a real simulator over a pass-through mapper — and NEVER
    // call its send methods (no SendInput ever fires in tests).
    // The mapper is NESTED in Win32InputSimulatorTestHelper — qualify it (no `using static`).
    private readonly Win32InputSimulator _sim =
        new(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());

    private static HashSet<ModifierKey> Mods(params ModifierKey[] keys) => new(keys);

    [Fact]
    public void ModifiedClickBody_BalancedDownUp_ForSingleClick()
    {
        var body = _sim.BuildModifiedClickBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), right: false, middle: false, doubleClick: false);

        Check.That(body).HasSize(2);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTDOWN)).IsNotEqualTo(0u);
        Check.That((body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
    }

    [Fact]
    public void ModifiedClickBody_DoubleClick_IsOneBatchOfFourMouseEvents()
    {
        var body = _sim.BuildModifiedClickBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), right: false, middle: false, doubleClick: true);

        Check.That(body).HasSize(4);
        Check.That(body.All(i => (i.Union.Mouse.Flags & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_LEFTUP)) != 0)).IsTrue();
    }

    [Fact]
    public void ModifiedWheelBody_LeadsWithAbsoluteMove_ThenClampedWheel()
    {
        var body = _sim.BuildModifiedWheelBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), deltaX: 0, deltaY: 3);

        Check.That(body).HasSize(2);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE)).IsNotEqualTo(0u);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE)).IsNotEqualTo(0u);
        Check.That((body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL)).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);
    }

    [Fact]
    public void Wrap_DownsPrecedeBody_MaskBeforeAltUp_UpsReversed()
    {
        var body = new[]
        {
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
        };

        var batch = Win32InputSimulator.WrapWithModifiers(
            Mods(ModifierKey.Shift, ModifierKey.Alt), body, includeAltMenuMask: true);

        // Down order: Alt-down, Shift-down. Body. Ups REVERSED: Shift-up at [4], mask pair
        // immediately before the Alt-up at [5..6], Alt-up at [7].
        Check.That(batch).HasSize(2 + 2 + 2 + 2);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[2].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[3].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[4].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[4].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[5].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[6].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[7].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[7].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void Wrap_NoAlt_NoMaskPair()
    {
        var body = new[] { Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN) };
        var batch = Win32InputSimulator.WrapWithModifiers(Mods(ModifierKey.Shift), body, includeAltMenuMask: true);

        Check.That(batch).HasSize(1 + 1 + 1);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void Wrap_NullModifiers_ZeroKeyboardInputs_AndKeyboardSequence_NeverCarriesTheMask()
    {
        // The null-mods arm: WrapWithModifiers(null, body, true) must emit the body UNCHANGED.
        var body = new[] { Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN) };
        var wrapped = Win32InputSimulator.WrapWithModifiers(null, body, includeAltMenuMask: true);
        Check.That(wrapped).HasSize(1);
        Check.That(wrapped[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            Mods(ModifierKey.Alt, ModifierKey.Shift), 0x41, isExtendedKey: false);

        Check.That(batch).HasSize(4 + 2);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void Compensation_EmitsMouseUpBeforeModifierUps_MaskBeforeAltUp()
    {
        // Batch: Alt-down, LEFTDOWN, LEFTUP, LEFTDOWN, Alt-up — sent = 4, so the second
        // LEFTDOWN landed without its up and the Alt-up never landed.
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulator.BuildCompensatingReleases(4, batch);

        Check.That(releases).HasSize(4); // LEFTUP, mask-down, mask-up, Alt-up
        Check.That((releases[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That((releases[3].Union.Keyboard.Flags & NativeMethods.KEYEVENTF_KEYUP)).IsNotEqualTo(0u);
    }

    [Fact]
    public void Compensation_FullySentBatch_EmitsNothing_EarlyReturn()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP),
        };

        Check.That(Win32InputSimulator.BuildCompensatingReleases(4, batch)).IsEmpty();
    }

    [Fact]
    public void Compensation_NoAlt_NoMaskInReleases()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulator.BuildCompensatingReleases(2, batch); // LEFTDOWN + Shift-down unmatched

        Check.That(releases).HasSize(2); // LEFTUP first, then Shift-up; no Alt means no mask
        Check.That((releases[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(releases.Any(i => i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }
}

/// <summary>Test access to the internal composition helpers (InternalsVisibleTo covers this project).</summary>
internal static class Win32InputSimulatorTestHelper
{
    /// <summary>Identity-ish mapper: the composition tests assert flags and types, never coordinates. IMouseCoordinateMapper lives in EyeCtrl.Platform.Input and returns (int, int).</summary>
    public sealed class PassThroughCoordinateMapper : global::EyeCtrl.Platform.Input.IMouseCoordinateMapper
    {
        public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(
            EyeCtrl.Core.Models.PhysicalPoint point, int screenWidth, int screenHeight)
            => ((int)point.X, (int)point.Y); // PhysicalPoint is (double X, double Y) — no implicit narrowing
    }

    public static NativeMethods.INPUT MouseInput(uint flags) => new()
    {
        Type = NativeMethods.INPUT_MOUSE,
        Union = new NativeMethods.INPUTUNION { Mouse = new NativeMethods.MOUSEINPUT { Flags = flags } },
    };

    public static NativeMethods.INPUT KeyboardInput(ushort vk, uint flags) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Union = new NativeMethods.INPUTUNION { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = vk, Flags = flags } },
    };
}
