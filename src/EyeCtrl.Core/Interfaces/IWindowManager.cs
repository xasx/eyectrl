namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Enumerates top-level desktop windows, brings a window to the foreground, and
/// performs window-management actions (minimize / maximize-or-restore / close) on
/// the foreground window. Used by the Task Switcher (<c>EnumerateOpenWindows</c> /
/// <c>BringToFront</c>) and by the window-management actions on the Quick Actions
/// toolbar.
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
    /// <remarks>
    /// Returns at once. The implementation runs the restore/ALT-grant/foreground sequence on a
    /// background worker, because the caller is the UI thread, which is also the gaze-dwell thread,
    /// and every step is a synchronous cross-process call that waits on the target. The sequence is
    /// serialised so two picks never interleave their ALT grants; a target that stops answering is
    /// abandoned after a bounded hand-off, and a request that cannot start within about a second is
    /// dropped rather than fired late.
    /// </remarks>
    void BringToFront(IntPtr hwnd);

    /// <summary>
    /// Returns the foreground window's HWND if it is eligible for window-management actions
    /// (not the shell, not this process, still a valid window), or <c>null</c> if ineligible.
    /// Fail-closed: callers must no-op on <c>null</c>. Read at op-fire time, not stashed.
    /// </summary>
    IntPtr? TryGetEligibleForegroundWindow();

    /// <summary>Minimizes the window. Fire-and-forget: the caller never waits on the target.</summary>
    /// <remarks>
    /// The implementation posts <c>ShowWindowAsync</c>, which returns without waiting for the
    /// target's thread — a hung window cannot stall the UI/gaze thread. Minimise has no ordering
    /// requirement and no foreground grant, so it needs no worker. For a SET of windows — "show
    /// desktop" — use <see cref="MinimizeNonBlocking"/>; it is the same mechanism.
    /// </remarks>
    void Minimize(IntPtr hwnd);

    /// <summary>
    /// Requests a minimise WITHOUT waiting for the target to process it: the implementation keeps
    /// the <c>IsWindow</c> re-check and then posts the request asynchronously
    /// (<c>ShowWindowAsync</c>). Callers that act on many windows in one go must use this: the
    /// task switcher's "show desktop" runs on the UI thread, which is also the gaze-dwell thread,
    /// so one unresponsive window must not freeze the owner's control.
    /// </summary>
    /// <remarks>Fire-and-forget: no result is reported, exactly like <see cref="Minimize"/>.</remarks>
    void MinimizeNonBlocking(IntPtr hwnd);

    /// <summary>
    /// Maximizes a normal window or restores a maximized/minimized one. Fire-and-forget: the
    /// implementation reads the state and posts <c>ShowWindowAsync</c>, so the caller never waits
    /// on the target and a hung window cannot stall the UI/gaze thread.
    /// </summary>
    void MaximizeOrRestore(IntPtr hwnd);

    /// <summary>Closes the window gracefully via WM_CLOSE (the app's save-changes prompt fires).</summary>
    /// <returns><c>true</c> if the message was posted; UIPI-blocked sends to elevated windows return <c>false</c>.</returns>
    bool CloseWindow(IntPtr hwnd);
}
