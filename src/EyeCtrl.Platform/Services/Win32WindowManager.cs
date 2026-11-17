namespace EyeCtrl.Platform.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using Microsoft.Extensions.Logging;

/// <summary>
/// Win32-backed implementation of <see cref="IWindowManager"/> that enumerates
/// top-level desktop windows via <c>EnumWindows</c> and activates a window with
/// <c>SetForegroundWindow</c>. Used by the Task Switcher. Also implements
/// <see cref="IEscapeWindowService"/>, the non-injecting way out of a UIPI block.
/// </summary>
public class Win32WindowManager : IWindowManager, IEscapeWindowService
{
    /// <summary>
    /// How long the escape fallback waits for the target to take the foreground after the
    /// minimize/restore toggle. Short: a responsive target honours the toggle in milliseconds and
    /// the owner is waiting on the result; the worker is the only thing that ever waits.
    /// </summary>
    internal static readonly TimeSpan DefaultEscapeFallbackWait = TimeSpan.FromMilliseconds(500);

    /// <summary>The poll interval while a worker waits for the target to answer a state change.</summary>
    private static readonly TimeSpan EscapePollInterval = TimeSpan.FromMilliseconds(10);

    private readonly ILogger<Win32WindowManager> _logger;
    private readonly IInputIntegrityProbe _integrity;
    private readonly IWindowActionNative _native;
    private readonly WindowActionScheduler _actions;
    private readonly TimeSpan _escapeFallbackWait;

    /// <summary>Creates the window manager.</summary>
    /// <param name="logger">Records why a switch failed — see <see cref="BringToFront"/>.</param>
    /// <param name="integrity">Reads token facts, so the escape path never targets a window
    /// above EyeCtrl's own reach.</param>
    public Win32WindowManager(ILogger<Win32WindowManager> logger, IInputIntegrityProbe integrity)
        : this(logger, integrity, new Win32WindowActionNative(), new WindowActionScheduler(logger))
    {
    }

    /// <summary>
    /// Test seam: substitutes the Win32 calls and shortens the scheduler's bounds so a fake "hung"
    /// target can prove the caller never waits on it.
    /// </summary>
    internal Win32WindowManager(
        ILogger<Win32WindowManager> logger,
        IInputIntegrityProbe integrity,
        IWindowActionNative native,
        WindowActionScheduler actions,
        TimeSpan? escapeFallbackWait = null)
    {
        _logger = logger;
        _integrity = integrity;
        _native = native;
        _actions = actions;
        _escapeFallbackWait = escapeFallbackWait ?? DefaultEscapeFallbackWait;
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

            // A minimised window passes every filter above (visible, titled, unowned) but is not on
            // screen — selecting its tile RESTORES it, which moves whatever the owner is looking at.
            // Read here so the tile can say so; the switch path already restores iconic windows
            // (see BringToFront). Read at enumeration, like the title and the process name: the
            // snapshot is one-shot per switcher session.
            windows.Add(new AppWindow(hWnd, title, processId, processName, NativeMethods.IsIconic(hWnd)));
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
    /// <remarks>
    /// Returns at once: the whole sequence runs on <see cref="WindowActionScheduler"/>'s worker,
    /// because the caller is the UI dispatcher, which is also the gaze-dwell thread, and every call
    /// below is a synchronous cross-process wait on the target. A target that stops answering is
    /// abandoned after the hand-off bound; a request that cannot start within the start deadline is
    /// dropped — an ALT grant must never be injected long after the dwell that asked for it.
    /// </remarks>
    public void BringToFront(IntPtr hwnd)
    {
        _actions.Enqueue("bring a window to the foreground", hwnd, action =>
        {
            if (action.IsCancelled) return;

            if (hwnd == IntPtr.Zero) return;

            // The tile snapshot is one-shot (AppWindow per ShowOverlay), so a window that closes
            // mid-session leaves a tile whose dwell dispatches here on a dead hwnd. Today that
            // path injects the ALT pair unconditionally — a stray ALT tap into whatever currently
            // holds foreground (the 2026-08-25 stuck-ALT hazard) — and only the later
            // SetForegroundWindow fails and logs. IsWindow is authoritative at the point of use;
            // skip the grant for a target that cannot receive it (the same guard Minimize,
            // MaximizeOrRestore and CloseWindow already carry).
            if (!_native.IsWindow(hwnd))
            {
                _logger.LogWarning(
                    "Task switcher will not foreground window {Hwnd}: the window is gone (dead hwnd) — "
                    + "skipping the ALT foreground grant.",
                    hwnd);
                return;
            }

            // Minimized first (2026-08-18 field follow-up: Edge/Word/Task-Manager switched fine but
            // WhatsApp/ZCode/Claude — all iconic — did not): SetForegroundWindow "succeeds" on a
            // minimized window without un-minimizing it, so the window never visibly appears and
            // the success return skips any fallback. Restoring before foregrounding is the textbook
            // sequence.
            if (_native.IsIconic(hwnd))
                _native.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            // The restore above is the call that hangs on a window that has stopped answering. If a
            // later pick gave up on us while we were inside it, the ALT grant below must NOT be
            // injected now: by the time the target answers, the foreground belongs to whatever the
            // owner moved on to, and a stale tap lands there (the 2026-08-25 key-misdelivery class,
            // with the additional twist that the tap would arrive seconds late).
            if (action.IsCancelled) return;

            // Same rule when nothing later cancelled us, with the coordinator's 3 s budget: a
            // target that answered the restore very late has recovered too late for the switch,
            // and the ALT grant would land seconds after the dwell. Drop it and say so, rather than
            // perform the switch into whatever holds the foreground by now.
            if (action.IsStale)
            {
                _logger.LogWarning(
                    "Task switcher will not foreground window {Hwnd}: {ElapsedMs:0} ms after the "
                    + "request the target still has not taken the foreground, so the ALT grant would "
                    + "arrive too late — dropping the switch.",
                    hwnd, action.Elapsed.TotalMilliseconds);
                return;
            }

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
            var injected = _native.SendInput(
                (uint)altTap.Length, altTap, Marshal.SizeOf<NativeMethods.INPUT>());
            // Read only when the grant came up short: GetLastPInvokeError is undefined after a call
            // that succeeded, and a meaningless code in a diagnostic log is worse than no code.
            var grantError = injected < (uint)altTap.Length ? Marshal.GetLastPInvokeError() : 0;
            var ok = _native.SetForegroundWindow(hwnd);

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
            // window through a shell-honored state change that also brings it forward. It must not
            // run once the pick has been abandoned or has run past its budget: SW_RESTORE activates
            // the window, and a late toggle would take activation away from the pick the owner made
            // next. The failure warning above is written before these guards because it records the
            // failure itself, which is the diagnosis even when the fallback is skipped.
            if (action.IsCancelled || action.IsStale) return;
            _native.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            if (action.IsCancelled || action.IsStale) return;
            _native.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        });
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
        // ShowWindowAsync, not ShowWindow: the caller is the gaze-dwell thread, and the synchronous
        // form waits for the target's thread, so one hung window would freeze every overlay —
        // Pause included. Minimise needs no ordering and no foreground grant, so it needs neither
        // a worker nor the scheduler: the async form posts the request and returns.
        if (!_native.IsWindow(hwnd)) return;
        _native.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <inheritdoc />
    public void MinimizeNonBlocking(IntPtr hwnd)
    {
        // ShowWindowAsync, not ShowWindow: "show desktop" minimises every enumerated window from
        // the UI thread, which is also the gaze-dwell thread. A synchronous ShowWindow waits for
        // the target's thread to process the request, so one hung window ("Keine Rückmeldung")
        // would freeze every overlay — including Pause — and take the owner's control away. The
        // async form posts the request and returns; a hung target simply handles it late (or not
        // at all), which is what a hung window can do anyway.
        if (!NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <inheritdoc />
    public void MaximizeOrRestore(IntPtr hwnd)
    {
        // The same reasoning as Minimize: local state reads only, then the non-blocking show.
        // WindowStateOps keeps the decision identical to the synchronous path this replaced.
        if (!_native.IsWindow(hwnd)) return;
        var op = WindowStateOps.DecideWindowOp(
            isZoomed: _native.IsZoomed(hwnd),
            isIconic: _native.IsIconic(hwnd));
        _native.ShowWindowAsync(hwnd, op == WindowStateOp.Maximize
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

    /// <inheritdoc />
    public IntPtr? TryGetEscapeTarget()
    {
        var self = _integrity.ReadSelf();
        var ownProcessId = (uint)Environment.ProcessId;
        var shell = NativeMethods.GetShellWindow();
        var candidates = new List<EscapeCandidate>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            // EnumWindows walks the Z-order top-down, so the first eligible window is the one
            // directly beneath the blocking foreground — the "previous window" the owner means.
            if (!IsCandidateWindow(hwnd, out _))
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var isShell = hwnd == shell;
            var facts = isShell || pid == ownProcessId
                ? ProcessTokenFacts.Unknown
                : _integrity.InspectWindow(hwnd);

            candidates.Add(new EscapeCandidate(hwnd, pid, isShell, facts));
            return true;
        }, IntPtr.Zero);

        // The shell is the last resort even when it has no title (IsCandidateWindow would drop it).
        if (shell != IntPtr.Zero && !candidates.Exists(c => c.Hwnd == shell))
        {
            NativeMethods.GetWindowThreadProcessId(shell, out var shellPid);
            candidates.Add(new EscapeCandidate(shell, shellPid, IsShell: true, ProcessTokenFacts.Unknown));
        }

        return EscapeTargetRules.SelectTopmost(candidates, self, ownProcessId);
    }

    /// <inheritdoc />
    public void BeginBringToFront(IntPtr hwnd, Action<EscapeWindowResult?> onCompleted)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);

        // The caller is the UI dispatcher, which is also the gaze-dwell thread. Nothing below may
        // run there: the scheduler's worker keeps every cross-process call off it, and the entry
        // point returns before the target is touched. Escape and pick share the scheduler, so the
        // two actions never run at once — a hung pick cannot run alongside a hung escape.
        EscapeWindowResult? result = null;
        _actions.Enqueue(
            "bring the escape window to the foreground",
            hwnd,
            action => result = RunEscapeAttempt(hwnd, action),
            completion => onCompleted(
                completion == WindowActionCompletion.Ran ? result : null));
    }

    /// <summary>
    /// One escape attempt on the scheduler's worker. Returns null when the attempt stopped without
    /// a result (abandoned by a later action, or past its completion budget); the one Warning for
    /// either case is written here or by the scheduler.
    /// </summary>
    private EscapeWindowResult? RunEscapeAttempt(IntPtr hwnd, WindowActionContext action)
    {
        if (action.IsCancelled)
            return null;

        if (hwnd == IntPtr.Zero)
            return new EscapeWindowResult(false, false, false);

        var isShell = hwnd == _native.GetShellWindow();

        // A minimized target is restored first, for the reason the task switcher documents: a bare
        // SetForegroundWindow "succeeds" on an iconic window without un-minimizing it. Never on the
        // shell: minimizing and restoring the desktop's Progman window is untried, so the shell path
        // is SetForegroundWindow ONLY.
        //
        // ShowWindowAsync, not ShowWindow: the synchronous form waits for the target's thread, and
        // a hung window would pin this worker (it used to pin the UI thread right here). The wait
        // below is what keeps the order's meaning: SetForegroundWindow must not run until the
        // target has actually left the iconic state, or it "succeeds" without showing the window.
        if (!isShell && _native.IsIconic(hwnd))
        {
            // SW_RESTORE activates the target, so it must not be posted once the attempt is
            // abandoned or past its budget either.
            if (!StillFresh(hwnd, action))
                return null;

            _native.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            if (!WaitForRestore(hwnd, action))
                return null;
        }

        // DELIBERATELY no ALT tap: that grant is injected input, and the state this path exists to
        // escape is one where injected input is silently dropped. SetForegroundWindow alone may be
        // refused by the foreground lock; the ShowWindow toggle below is the non-injecting
        // fallback the ticket asks for. Whether either works under an elevated foreground is the
        // sitting question (unverified here).
        var setForeground = _native.SetForegroundWindow(hwnd);
        if (_native.GetForegroundWindow() == hwnd)
        {
            // A switch that only completed after the budget is as stale as a pick would be: do not
            // report it as a finished attempt (the owner may have moved on). Nothing is undone —
            // minimising the target now would take the screen away a second time.
            if (!StillFresh(hwnd, action))
                return null;

            _logger.LogInformation(
                "Blocked-input escape: brought window {Hwnd} ({Process}) to the foreground without injecting input.",
                hwnd, DescribeProcess(hwnd));
            return new EscapeWindowResult(true, setForeground, false);
        }

        if (!StillFresh(hwnd, action))
            return null;

        if (isShell)
        {
            _logger.LogWarning(
                "Blocked-input escape: SetForegroundWindow returned {SetForeground} for the desktop shell "
                + "{Hwnd}; no minimize/restore is attempted on the shell.",
                setForeground, hwnd);
            return new EscapeWindowResult(false, setForeground, false);
        }

        // The fallback's minimize -> restore ORDER is the point: SW_RESTORE activates the window, so
        // issuing minimize then restore moves it forward. Both are posted, never synchronous, and
        // the foreground check below runs here on the worker after a short bounded poll. A late
        // activating restore must not run once the attempt was abandoned or has run past its
        // budget: it would take activation away from whatever the owner moved on to.
        _native.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
        if (!StillFresh(hwnd, action))
            return null;
        _native.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        if (!StillFresh(hwnd, action))
            return null;

        var waited = WaitForFallbackForeground(hwnd, action);
        if (waited == EscapeFallbackWait.Stopped)
            return null;

        var changed = waited == EscapeFallbackWait.ForegroundChanged;
        if (changed)
        {
            _logger.LogInformation(
                "Blocked-input escape: SetForegroundWindow returned {SetForeground} for {Hwnd} ({Process}); "
                + "the minimize/restore fallback brought it forward without injecting input.",
                setForeground, hwnd, DescribeProcess(hwnd));
        }
        else
        {
            _logger.LogWarning(
                "Blocked-input escape: neither SetForegroundWindow ({SetForeground}) nor the "
                + "minimize/restore fallback moved {Hwnd} ({Process}) to the foreground.",
                setForeground, hwnd, DescribeProcess(hwnd));
        }

        return new EscapeWindowResult(changed, setForeground, true);
    }

    /// <summary>
    /// True while the attempt may take its remaining steps. An abandonment is silent here — the
    /// scheduler already wrote its one Warning — while running past the completion budget writes
    /// the scheduler's one timeout Warning and stops the remaining steps.
    /// </summary>
    private bool StillFresh(IntPtr hwnd, WindowActionContext action)
    {
        if (action.IsCancelled)
            return false;

        if (action.IsStale)
        {
            _logger.LogWarning(
                "Window action {Action} for window {Hwnd} was abandoned after {ElapsedMs:0} ms: the "
                + "blocked-input escape ran past its completion budget, so its remaining steps were "
                + "dropped.",
                "bring the escape window to the foreground", hwnd, action.Elapsed.TotalMilliseconds);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Waits on the worker, bounded by the attempt's completion budget, until the restored target
    /// has actually left the iconic state. False stops the attempt (abandoned or timed out).
    /// </summary>
    private bool WaitForRestore(IntPtr hwnd, WindowActionContext action)
    {
        while (_native.IsIconic(hwnd))
        {
            if (!StillFresh(hwnd, action))
                return false;

            Thread.Sleep(EscapePollInterval);
        }

        return StillFresh(hwnd, action);
    }

    /// <summary>
    /// Polls for the fallback's foreground change on the worker. Bounded by the shorter of the
    /// fallback window and the attempt's completion budget, so a dead target ends in today's
    /// failure line instead of a stuck worker.
    /// </summary>
    private EscapeFallbackWait WaitForFallbackForeground(IntPtr hwnd, WindowActionContext action)
    {
        var elapsed = Stopwatch.StartNew();
        while (_native.GetForegroundWindow() != hwnd)
        {
            if (!StillFresh(hwnd, action))
                return EscapeFallbackWait.Stopped;

            if (elapsed.Elapsed >= _escapeFallbackWait)
                return EscapeFallbackWait.StillRefused;

            Thread.Sleep(EscapePollInterval);
        }

        return EscapeFallbackWait.ForegroundChanged;
    }

    /// <summary>The fallback's poll outcome.</summary>
    private enum EscapeFallbackWait
    {
        /// <summary>The target ended up in the foreground.</summary>
        ForegroundChanged,

        /// <summary>The short fallback wait ran out while the target still refused the foreground.</summary>
        StillRefused,

        /// <summary>The attempt was abandoned or timed out while the poll was running.</summary>
        Stopped,
    }

    /// <summary>
    /// The process name for the escape logs (C10 fixup-01 F7): window titles carry document, mail
    /// and chat names, and the brief allows process names only.
    /// </summary>
    private string DescribeProcess(IntPtr hwnd) =>
        _integrity.InspectWindow(hwnd).ProcessName ?? "unknown";
}
