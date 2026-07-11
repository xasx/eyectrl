namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Enumerates top-level desktop windows and brings a window to the foreground.
/// Used by the Task Switcher to populate its tile list and to activate a selected window.
/// </summary>
public interface IWindowManager
{
    /// <summary>
    /// Enumerates the currently open top-level windows on the desktop.
    /// Excludes invisible windows, tool windows (such as EyeCtrl's own overlays),
    /// and child/owned windows so that only user-visible application windows are returned.
    /// </summary>
    /// <returns>A snapshot of open windows. Never null, but may be empty.</returns>
    IReadOnlyList<AppWindow> EnumerateOpenWindows();

    /// <summary>
    /// Brings the specified window to the foreground.
    /// </summary>
    /// <param name="hwnd">Win32 window handle of the window to activate.</param>
    void BringToFront(IntPtr hwnd);
}
