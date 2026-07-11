namespace EyeCtrl.Core.Models;

/// <summary>
/// Describes a single top-level window discovered on the desktop, for use by the
/// Task Switcher overlay.
/// </summary>
/// <param name="Hwnd">Win32 window handle of the top-level window.</param>
/// <param name="Title">Window title text (from <c>GetWindowText</c>). Never null or whitespace.</param>
/// <param name="ProcessId">Win32 process id that owns the window.</param>
/// <param name="ProcessName">Friendly process name, or an empty string when the process could not be queried.</param>
public sealed record AppWindow(IntPtr Hwnd, string Title, uint ProcessId, string ProcessName);
