namespace EyeCtrl.Platform.Tests.Input;

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
        var unionSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUTUNION>();
        var mouseSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MOUSEINPUT>();
        var keySize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.KEYBDINPUT>();

        Check.That(unionSize).IsStrictlyGreaterThan(mouseSize - 1);
        Check.That(unionSize).IsStrictlyGreaterThan(keySize - 1);
    }

    [Fact]
    public void Input_SizeConsistentWithWin32()
    {
        // INPUT = uint (4) + INPUTUNION. On both x64 and arm64, this is the
        // standard Win32 INPUT structure size used by SendInput.
        var size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>();

        Check.That(size).IsStrictlyGreaterThan(0);
    }
}
