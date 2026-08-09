namespace EyeCtrl.Platform.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

/// <summary>
/// Win32-backed implementation of <see cref="IWindowManager"/> that enumerates
/// top-level desktop windows via <c>EnumWindows</c> and activates a window with
/// <c>SetForegroundWindow</c>. Used by the Task Switcher.
/// </summary>
public class Win32WindowManager : IWindowManager
{
    /// <summary>
    /// Enumerates the currently open top-level windows on the desktop.
    /// Excludes invisible windows, tool windows (such as EyeCtrl's own overlays,
    /// which carry <c>WS_EX_TOOLWINDOW</c>), and child/owned windows so that only
    /// user-visible application windows are returned.
    /// </summary>
    /// <returns>A snapshot of open windows. Never null, but may be empty.</returns>
    public IReadOnlyList<AppWindow> EnumerateOpenWindows()
    {
        var windows = new List<AppWindow>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsCandidateWindow(hWnd, out var title))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
            string processName = "";
            try
            {
                using var proc = Process.GetProcessById((int)processId);
                processName = proc.ProcessName;
            }
            catch
            {
                // Process may have exited or be inaccessible
            }

            windows.Add(new AppWindow(hWnd, title, processId, processName));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    internal static bool IsCandidateWindow(IntPtr hWnd, out string title)
    {
        title = "";

        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        // Exclude tool windows (all EyeCtrl overlays have WS_EX_TOOLWINDOW)
        var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        // Exclude child windows (only top-level windows with no owner)
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
            return false;

        // Exclude cloaked windows: suspended UWP apps and ApplicationFrameWindow "ghosts"
        // pass IsWindowVisible but are not actually shown. Only trust the result on S_OK.
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // Get title — use char[] buffer (not StringBuilder; [LibraryImport] doesn't support it).
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0)
            return false;
        var buffer = new char[length + 1];
        int copied = NativeMethods.GetWindowText(hWnd, buffer, buffer.Length);
        title = new string(buffer, 0, copied);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        return true;
    }

    /// <summary>
    /// Brings the specified window to the foreground. Calls
    /// <c>AllowSetForegroundWindow(ASFW_ANY)</c> first so that foreground
    /// restrictions do not block the activation.
    /// </summary>
    /// <param name="hwnd">Win32 window handle of the window to activate.</param>
    public void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}
