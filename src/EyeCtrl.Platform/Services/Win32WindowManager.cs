namespace EyeCtrl.Platform.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using Microsoft.Extensions.Logging;

/// <summary>
/// Win32-backed implementation of <see cref="IWindowManager"/> that enumerates
/// top-level desktop windows via <c>EnumWindows</c> and activates a window with
/// <c>SetForegroundWindow</c>. Used by the Task Switcher.
/// </summary>
public class Win32WindowManager : IWindowManager
{
    private readonly ILogger<Win32WindowManager> _logger;

    /// <summary>Creates the window manager.</summary>
    /// <param name="logger">Records why a switch failed — see <see cref="BringToFront"/>.</param>
    public Win32WindowManager(ILogger<Win32WindowManager> logger)
    {
        _logger = logger;
    }

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
        // Use GetWindowText's return (chars actually copied), not GetWindowTextLength, so a
        // title that shrank between the two calls doesn't leave trailing '\0's in the string.
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
    /// Brings the specified window to the foreground. Simulates an ALT tap first —
    /// a process seen delivering keyboard input earns the foreground right that
    /// <c>SetForegroundWindow</c> otherwise silently refuses for this non-activating
    /// overlay process.
    /// </summary>
    /// <param name="hwnd">Win32 window handle of the window to activate.</param>
    public void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Minimized first (2026-08-18 field follow-up: Edge/Word/Task-Manager switched fine but
        // WhatsApp/ZCode/Claude — all iconic — did not): SetForegroundWindow "succeeds" on a
        // minimized window without un-minimizing it, so the window never visibly appears and
        // the success return skips any fallback. Restoring before foregrounding is the textbook
        // sequence.
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        // The overlays are WS_EX_NOACTIVATE, so this process never holds foreground, and the
        // Windows foreground lock silently rejects a bare SetForegroundWindow: switcher dwell
        // selections fired, the overlay closed, and no window came forward (2026-08-18 field
        // diagnosis — the "tasks are not dwellable" report was really "switching never
        // happens"). The previous AllowSetForegroundWindow(ASFW_ANY) call was a no-op: it
        // delegates a right the caller must already hold. The ALT-press simulation is the
        // standard grant — a process seen delivering keyboard input earns the right to set the
        // foreground window.
        //
        // The tap must be one atomic SendInput pair issued BEFORE the switch. The old
        // keybd_event bracket straddled SetForegroundWindow, so the window holding focus when
        // ALT went down got the WM_SYSKEYDOWN while the matching WM_SYSKEYUP was redirected to
        // the new foreground window — leaving the old app with ALT latched (menu mode,
        // Alt-shortcut misfires), intermittently, depending on which app held focus. A gaze-only
        // user has no physical ALT press to clear that state, so it survived until reboot
        // (2026-08-25 field report: "alt key seems stuck"). Atomic delivery keeps both events in
        // one thread queue, and the grant still counts the completed tap.
        var altTap = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.INPUTUNION
                {
                    Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = NativeMethods.VK_MENU }
                }
            },
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.INPUTUNION
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        VirtualKey = NativeMethods.VK_MENU,
                        Flags = NativeMethods.KEYEVENTF_KEYUP
                    }
                }
            }
        };
        var injected = NativeMethods.SendInput(
            (uint)altTap.Length, altTap, Marshal.SizeOf<NativeMethods.INPUT>());
        // Read only when the grant came up short: GetLastPInvokeError is undefined after a call
        // that succeeded, and a meaningless code in a diagnostic log is worse than no code.
        var grantError = injected < (uint)altTap.Length ? Marshal.GetLastPInvokeError() : 0;
        var ok = NativeMethods.SetForegroundWindow(hwnd);

        // Both previous failures of this path were reported as bare symptoms — "tasks are not
        // dwellable" (2026-08-18) and "the alt key seems stuck" (2026-08-25) — and each cost a
        // diagnosis round, because the two calls that can fail here threw away everything they
        // knew. The log line is the whole point: a gaze-only user cannot bisect this by hand.
        // Named arguments: both counts are uint, so a swap would invert the diagnosis silently.
        var outcome = ForegroundSwitchDiagnosis.Classify(new ForegroundSwitchAttempt(
            InjectedInputs: injected,
            ExpectedInputs: (uint)altTap.Length,
            SetForegroundSucceeded: ok));
        if (outcome == ForegroundSwitchOutcome.Switched)
        {
            _logger.LogInformation("Task switcher brought window {Hwnd} to the foreground.", hwnd);
            return;
        }

        // The title is the diagnostic payload on this path: last time, which apps failed
        // (WhatsApp, Claude — all minimized) against which worked (Edge, Word) was the finding.
        _logger.LogWarning(
            "Task switcher could not foreground window {Hwnd} ({Title}): {Outcome}. "
            + "SendInput accepted {Injected}/{Expected} events (error {GrantError}); "
            + "falling back to a minimize/restore toggle.",
            hwnd, DescribeWindow(hwnd), outcome, injected, altTap.Length, grantError);

        // Rare fallback when even the grant is denied: a minimize/restore toggle moves the
        // window through a shell-honored state change that also brings it forward.
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
    }

    /// <summary>
    /// Window title for a log line, or "no title" when the handle no longer names a candidate
    /// window — a window that vanished mid-switch is itself a diagnosis worth reading.
    /// </summary>
    private static string DescribeWindow(IntPtr hwnd)
        => IsCandidateWindow(hwnd, out var title) ? title : "no title";

    /// <inheritdoc />
    public IntPtr? TryGetEligibleForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var shellHwnd = NativeMethods.GetShellWindow();

        // GetClassName: allocate a buffer (256 is the documented max for a window class name).
        var classNameBuffer = new char[256];
        int len = NativeMethods.GetClassName(hwnd, classNameBuffer, classNameBuffer.Length);
        var className = len > 0 ? new string(classNameBuffer, 0, len) : string.Empty;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        var candidate = new ForegroundWindowCandidate(
            hwnd,
            IsWindow: NativeMethods.IsWindow(hwnd),
            ShellWindowHwnd: shellHwnd,
            ClassName: className,
            ProcessId: pid,
            OwnProcessId: (uint)Environment.ProcessId);

        // IsEligible is fail-closed: zero/!IsWindow/shell/class/own-process all reject.
        return ForegroundWindowEligibility.IsEligible(candidate) ? hwnd : null;
    }

    /// <inheritdoc />
    public void Minimize(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <inheritdoc />
    public void MaximizeOrRestore(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        var op = WindowStateOps.DecideWindowOp(
            isZoomed: NativeMethods.IsZoomed(hwnd),
            isIconic: NativeMethods.IsIconic(hwnd));
        NativeMethods.ShowWindow(hwnd, op == WindowStateOp.Maximize
            ? NativeMethods.SW_MAXIMIZE
            : NativeMethods.SW_RESTORE);
    }

    /// <inheritdoc />
    public bool CloseWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        // WM_CLOSE is graceful: DefWindowProc derives it from SC_CLOSE. For a modal dialog
        // it posts IDCANCEL (like Esc) — closes the dialog, NOT the owning app.
        return NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
