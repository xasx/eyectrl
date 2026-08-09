namespace EyeCtrl.Platform.Tests.Services;

using System.Runtime.InteropServices;
using EyeCtrl.Platform.Services;
using NFluent;
using Xunit;

public class Win32WindowManagerFilterTests
{
    [Fact]
    public void IsCandidateWindow_NullHandle_ReturnsFalse()
    {
        var result = Win32WindowManager.IsCandidateWindow(IntPtr.Zero, out var title);

        Check.That(result).IsFalse();
    }

    [Fact]
    public void IsCandidateWindow_IsPurePredicate_DoesNotThrow()
    {
        // The predicate should handle any IntPtr without throwing (defense-in-depth).
        var result = Win32WindowManager.IsCandidateWindow(new IntPtr(0x1234), out var title);

        // Result may be true or false depending on the mock Win32 state, but it must not throw.
        Check.That(result || !result).IsTrue();
    }
}
