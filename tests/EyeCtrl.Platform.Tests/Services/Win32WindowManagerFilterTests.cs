namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;
using NFluent;
using Xunit;

public class Win32WindowManagerFilterTests
{
    // Only the null handle is asserted on. A fabricated non-zero handle (0x1234 and friends) is
    // *probably* not a window, but HWND values are assigned by the window manager and nothing
    // rules out a collision on a busy desktop — a test that can fail for a reason unrelated to
    // its name is worse than no test. Everything past the IsWindowVisible gate needs a real
    // window, which means a UI test.

    [Fact]
    public void IsCandidateWindow_NullHandle_ReturnsFalse()
    {
        // IsWindowVisible(NULL) is false, so the predicate rejects at the first gate.
        var result = Win32WindowManager.IsCandidateWindow(nint.Zero, out _);

        Check.That(result).IsFalse();
    }

    [Fact]
    public void IsCandidateWindow_Rejected_YieldsEmptyTitle()
    {
        // The out-param contract: callers read `title` only when the predicate returns true, but
        // it must still be a non-null empty string on the reject path (EnumerateOpenWindows passes
        // it straight into AppWindow).
        Win32WindowManager.IsCandidateWindow(nint.Zero, out var title);

        Check.That(title).IsEqualTo(string.Empty);
    }
}
