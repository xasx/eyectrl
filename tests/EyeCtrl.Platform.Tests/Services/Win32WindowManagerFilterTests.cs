namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;
using NFluent;
using Xunit;

public class Win32WindowManagerFilterTests
{
    [Fact]
    public void IsCandidateWindow_NullHandle_ReturnsFalse()
    {
        // IntPtr.Zero is not a real window — the predicate must reject it without touching Win32.
        var result = Win32WindowManager.IsCandidateWindow(nint.Zero, out var title);

        Check.That(result).IsFalse();
    }

    [Fact]
    public void IsCandidateWindow_InvalidHandle_ReturnsFalse()
    {
        // A fabricated handle is not a real window. The predicate should reject it
        // (or at minimum not throw) — it does NOT call EnumWindows, so it's safe headless.
        var result = Win32WindowManager.IsCandidateWindow(new nint(0x1234), out var title);

        Check.That(result).IsFalse();
    }
}
