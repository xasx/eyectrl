namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;

/// <summary>
/// Reads the OS's live left-button state for the held-button recovery's log line. Diagnostic
/// ONLY: whether a button-up is sent is decided by the cross-process marker, never by this probe —
/// the user may legitimately hold a second (physical or gaze) mouse while the marker is absent, and
/// must never have it released for them.
/// </summary>
public static class LeftButtonProbe
{
    private const int VkLeftButton = 0x01;

    /// <summary>
    /// True when Windows currently considers the left button down. <c>GetAsyncKeyState</c>'s high
    /// bit is "down as of the last message read"; the low bit (pressed since the last call) is
    /// deliberately ignored.
    /// </summary>
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
