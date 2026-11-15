namespace EyeCtrl.Core.Models;

/// <summary>
/// Describes a single top-level window discovered on the desktop, for use by the
/// Task Switcher overlay.
/// </summary>
/// <param name="Hwnd">Win32 window handle of the top-level window.</param>
/// <param name="Title">Window title text (from <c>GetWindowText</c>). Never null or whitespace.</param>
/// <param name="ProcessId">Win32 process id that owns the window.</param>
/// <param name="ProcessName">Friendly process name, or an empty string when the process could not be queried.</param>
/// <param name="IsMinimized">
/// True when the window is iconic (<c>IsIconic</c>) at enumeration time — it is on the taskbar, not
/// on screen, so the switcher draws the "Minimiert" badge on its tile. Switching to it still works
/// and still restores it (<c>BringToFront</c> restores an iconic window before foregrounding it);
/// this flag only lets the tile SAY so, because a restore moves the screen the owner is looking at.
/// Defaults to false so an enumeration that does not know the state describes an ordinary window.
/// </param>
public sealed record AppWindow(
    IntPtr Hwnd, string Title, uint ProcessId, string ProcessName, bool IsMinimized = false);
