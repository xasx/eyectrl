namespace EyeCtrl.Core.Models;

/// <summary>Inputs to the foreground-window eligibility check. Pure data — no Win32.</summary>
/// <param name="Hwnd">The foreground window handle under consideration.</param>
/// <param name="IsWindow">Result of Win32 <c>IsWindow()</c> on <paramref name="Hwnd"/>.</param>
/// <param name="ShellWindowHwnd">The HWND returned by Win32 <c>GetShellWindow()</c> (the Progman desktop).</param>
/// <param name="ClassName">The window's class name from <c>GetClassName()</c>.</param>
/// <param name="ProcessId">The window's owning process id from <c>GetWindowThreadProcessId()</c>.</param>
/// <param name="OwnProcessId">The current process's own pid — windows in our own process are rejected.</param>
public readonly record struct ForegroundWindowCandidate(
    IntPtr Hwnd,
    bool IsWindow,
    IntPtr ShellWindowHwnd,
    string ClassName,
    uint ProcessId,
    uint OwnProcessId);

/// <summary>
/// Decides whether a foreground HWND is safe to minimize/maximize/close. Pure: the caller
/// (Win32WindowManager) gathers the Win32 facts and passes them in. Fail-closed: anything
/// not explicitly eligible is rejected.
/// </summary>
public static class ForegroundWindowEligibility
{
    // Progman is the usual shell desktop; WorkerW layers over it under animated wallpaper
    // (NOT returned by GetShellWindow — the class denylist carries it); the two taskbar
    // classes cover single + multi-monitor.
    private static readonly HashSet<string> ShellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    };

    /// <summary>
    /// Returns <c>true</c> only when the candidate is a non-zero, extant, non-shell,
    /// non-self window. Any unrecognized input is rejected (fail-closed).
    /// </summary>
    /// <param name="c">The gathered foreground-window facts.</param>
    /// <returns><c>true</c> if safe to operate on; otherwise <c>false</c>.</returns>
    public static bool IsEligible(ForegroundWindowCandidate c)
    {
        if (c.Hwnd == IntPtr.Zero) return false;
        if (!c.IsWindow) return false;
        if (c.Hwnd == c.ShellWindowHwnd) return false;
        if (ShellClasses.Contains(c.ClassName)) return false;
        if (c.ProcessId == c.OwnProcessId) return false;
        return true;
    }
}
