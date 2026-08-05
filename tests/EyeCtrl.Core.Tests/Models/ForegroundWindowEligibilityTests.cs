namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class ForegroundWindowEligibilityTests
{
    private static readonly IntPtr Shell = new(0x10001);
    private static readonly IntPtr App = new(0x20002);
    private const uint AppPid = 1234u;
    private const uint SelfPid = 5678u;

    private static bool Eligible(IntPtr hwnd, bool isWindow, IntPtr shellHwnd,
        string className, uint processId)
        => ForegroundWindowEligibility.IsEligible(new ForegroundWindowCandidate(
            hwnd, isWindow, shellHwnd, className, processId, SelfPid));

    [Fact]
    public void NormalAppWindow_IsEligible()
        => Check.That(Eligible(App, isWindow: true, Shell, "Notepad", AppPid)).IsTrue();

    [Fact]
    public void ZeroHwnd_IsRejected()
        // isWindow:true so only the zero check is exercised (not the !IsWindow check).
        => Check.That(Eligible(IntPtr.Zero, isWindow: true, Shell, "Notepad", AppPid)).IsFalse();

    [Fact]
    public void DestroyedHwnd_IsRejected()
        => Check.That(Eligible(App, isWindow: false, Shell, "Notepad", AppPid)).IsFalse();

    [Fact]
    public void ShellDesktopHwnd_IsRejected()
        // className:"Notepad" so only the hwnd==shellHwnd check is exercised (not the class denylist).
        => Check.That(Eligible(Shell, isWindow: true, Shell, "Notepad", AppPid)).IsFalse();

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void ShellClasses_AreRejected(string className)
        => Check.That(Eligible(App, isWindow: true, Shell, className, AppPid)).IsFalse();

    [Fact]
    public void OwnProcess_IsRejected()
        => Check.That(Eligible(App, isWindow: true, Shell, "EyeCtrl.Overlay", SelfPid)).IsFalse();
}
