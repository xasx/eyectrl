namespace EyeCtrl.Platform.Tests.Input;

using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;
using NFluent;
using Xunit;

public class NativeMethodsLayoutTests
{
    [Fact]
    public void RectWidth_IsRightMinusLeft()
    {
        var rect = new NativeMethods.RECT { Left = 10, Top = 20, Right = 50, Bottom = 80 };

        Check.That(rect.Width).IsEqualTo(40);
    }

    [Fact]
    public void RectHeight_IsBottomMinusTop()
    {
        var rect = new NativeMethods.RECT { Left = 10, Top = 20, Right = 50, Bottom = 80 };

        Check.That(rect.Height).IsEqualTo(60);
    }

    [Fact]
    public void RectWidth_ZeroSize()
    {
        var rect = new NativeMethods.RECT { Left = 100, Top = 100, Right = 100, Bottom = 100 };

        Check.That(rect.Width).IsEqualTo(0);
        Check.That(rect.Height).IsEqualTo(0);
    }

    [Fact]
    public void InputUnion_SizeCoversLargestMember()
    {
        // INPUTUNION overlays MOUSEINPUT and KEYBDINPUT at FieldOffset(0).
        // Its size must be at least the max of the two to avoid truncation.
        var unionSize = Marshal.SizeOf<NativeMethods.INPUTUNION>();
        var mouseSize = Marshal.SizeOf<NativeMethods.MOUSEINPUT>();
        var keySize = Marshal.SizeOf<NativeMethods.KEYBDINPUT>();

        Check.That(unionSize).IsGreaterOrEqualThan(mouseSize);
        Check.That(unionSize).IsGreaterOrEqualThan(keySize);
    }

    [Fact]
    public void Input_SizeMatchesWin32()
    {
        // SendInput rejects a cbSize that isn't the OS's own sizeof(INPUT), so this is the one
        // number in the interop layer that must be exact: 40 bytes on any 64-bit architecture
        // (uint type + 8-byte-aligned union), 28 on 32-bit. Asserted unconditionally rather than
        // gated on architecture — a skipped assertion catches nothing.
        var expected = IntPtr.Size == 8 ? 40 : 28;

        Check.That(Marshal.SizeOf<NativeMethods.INPUT>()).IsEqualTo(expected);
    }
}
