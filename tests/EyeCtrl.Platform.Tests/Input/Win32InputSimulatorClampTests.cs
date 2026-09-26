namespace EyeCtrl.Platform.Tests.Input;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Platform.Input;
using NFluent;
using Xunit;

public class Win32InputSimulatorClampTests
{
    [Fact]
    public void ClampScrollDelta_WithinRange_Unchanged()
    {
        Check.That(Win32InputSimulator.ClampScrollDelta(100)).IsEqualTo(100);
        Check.That(Win32InputSimulator.ClampScrollDelta(-100)).IsEqualTo(-100);
        Check.That(Win32InputSimulator.ClampScrollDelta(0)).IsEqualTo(0);
    }

    [Fact]
    public void ClampScrollDelta_AboveMax_Clamped()
    {
        Check.That(Win32InputSimulator.ClampScrollDelta(20_000_000)).IsEqualTo(17_000_000);
    }

    [Fact]
    public void ClampScrollDelta_BelowMin_Clamped()
    {
        Check.That(Win32InputSimulator.ClampScrollDelta(-20_000_000)).IsEqualTo(-17_000_000);
    }

    [Fact]
    public void ClampScrollDelta_AtMaxBoundary_Unchanged()
    {
        Check.That(Win32InputSimulator.ClampScrollDelta(17_000_000)).IsEqualTo(17_000_000);
    }

    [Fact]
    public void BuildModifiedKeySequence_NoModifiers_HasTwoInputs()
    {
        var inputs = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey>(), 0x41, isExtendedKey: false);

        Check.That(inputs).HasSize(2); // key down + key up
        Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
        Check.That(inputs[0].Union.Keyboard.Flags).IsEqualTo(0);
        Check.That(inputs[1].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
    }

    [Fact]
    public void BuildModifiedKeySequence_WithModifiers_DownBeforeUp()
    {
        var modifiers = new HashSet<ModifierKey> { ModifierKey.Ctrl, ModifierKey.Shift };
        var inputs = Win32InputSimulator.BuildModifiedKeySequence(modifiers, 0x42, isExtendedKey: false);

        // 2 modifiers * 2 (down+up) + 2 (key down+up) = 6
        Check.That(inputs).HasSize(6);
        // First two are modifier downs (Ctrl, Shift in priority order)
        Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_CONTROL);
        Check.That(inputs[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        // Then key down + key up
        Check.That(inputs[2].Union.Keyboard.VirtualKey).IsEqualTo(0x42);
        Check.That(inputs[3].Union.Keyboard.VirtualKey).IsEqualTo(0x42);
        // Last two are modifier ups in REVERSE order (Shift, Ctrl)
        Check.That(inputs[4].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(inputs[5].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_CONTROL);
    }

    [Fact]
    public void BuildModifiedKeySequence_Alt_WrapsKeyInMenuModifier()
    {
        var inputs = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Alt }, 0x41, isExtendedKey: false);

        Check.That(inputs).HasSize(4);
        Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(inputs[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(inputs[1].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
        Check.That(inputs[2].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
        Check.That(inputs[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(inputs[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(inputs[3].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void BuildModifiedKeySequence_Win_UsesExtendedKeyFlag()
    {
        var inputs = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Win }, 0x41, isExtendedKey: false);

        Check.That(inputs).HasSize(4);
        Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_LWIN);
        Check.That(inputs[0].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(inputs[3].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    [Fact]
    public void BuildModifiedKeySequence_ExtendedMainKey_PropagatesExtendedFlag()
    {
        var inputs = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Ctrl }, 0x21, isExtendedKey: true);

        Check.That(inputs).HasSize(4);
        Check.That(inputs[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(inputs[2].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP);
    }
}
