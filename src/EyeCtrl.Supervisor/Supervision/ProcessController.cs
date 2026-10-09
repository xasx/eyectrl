namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

public sealed class ProcessController
{
    private IProcessHandle? _process;
    public IProcessHandle? BoundProcess => _process;

    /// <summary>
    /// Captured at launch/adopt. RESERVED for a future strict-mode <see cref="Ipc.ClientIdentityBinder"/>
    /// upgrade (PID-reuse protection via creation-time comparison). Currently captured-but-unread.
    /// </summary>
    public DateTime? StartTimeUtc { get; private set; }
    public string? ExePath { get; private set; }

    /// <summary>
    /// True when the supervisor itself initiated the most recent <see cref="ForceKill"/>. The Exited-event
    /// handler checks this to avoid double-counting: a supervisor-initiated kill is the EXPECTED consequence
    /// of a hang/gaze-death/startup-grace recovery that ALREADY booked a strike via GoRecovering. Feeding a
    /// second ProcessExited for it would count the same incident twice and halve the crash budget.
    /// Cleared on the next Launch/Adopt.
    /// </summary>
    public bool KillInitiated { get; private set; }

    /// <summary>Raised when the bound process exits. Detached on <see cref="Unbind"/>/rebind, so an
    /// exit of a process the supervisor no longer watches is not reported (ticket 06-B).</summary>
    public event Action<IProcessHandle>? BoundProcessExited;

    private Action? _onExited;

    private readonly Func<ProcessStartInfo, IProcessHandle?> _processFactory;
    private readonly Func<int, IProcessHandle?> _adoptFactory;

    public ProcessController()
        // Process.Start is documented to return null when no new process was started; the null-tolerant
        // factory signature keeps that out of Launch rather than turning it into a NullReferenceException.
        : this(psi => Process.Start(psi) is { } started ? new RealProcessHandle(started) : null,
               pid =>
               {
                   try { return new RealProcessHandle(Process.GetProcessById(pid)); }
                   catch { return null; }
               })
    {
    }

    public ProcessController(Func<ProcessStartInfo, IProcessHandle?> processFactory, Func<int, IProcessHandle?> adoptFactory)
    {
        _processFactory = processFactory;
        _adoptFactory = adoptFactory;
    }

    /// <summary>Starts the App and binds it.</summary>
    /// <param name="exePath">The App exe.</param>
    /// <param name="paused">Adds <c>--paused</c> (the FallenBack retry).</param>
    /// <param name="uiLanguage">A language the caretaker chose while the App was down (T05b): adds
    /// <c>--ui-language &lt;token&gt;</c>, which the App saves at startup. A launch that loses the
    /// App's single-instance guard exits before it reads the flag, so the caller keeps the choice
    /// pending until a re-read finds it saved.</param>
    public void Launch(string exePath, bool paused, UiLanguage? uiLanguage = null)
    {
        ReleasePrevious();
        KillInitiated = false;
        // The path we were handed IS the launched image; no need to ask the OS for it (and
        // Process.MainModule on a just-started process is one more thing that can throw).
        ExePath = exePath;
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        if (uiLanguage is { } language)
        {
            // T01's single token mapping; the App parses it with the same table.
            psi.ArgumentList.Add("--ui-language");
            psi.ArgumentList.Add(UiLanguageTokens.ToCommandLine(language));
        }
        Bind(_processFactory(psi));
        StartTimeUtc = _process?.StartTime;
    }

    /// <summary>
    /// Binds an externally-launched process by PID. Returns false if the PID is not running / cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para>Must not throw: the caller is a <c>Dispatcher.BeginInvoke</c> heartbeat callback
    /// (<c>Program.OnHeartbeatReceived</c>), where an escaping exception becomes a
    /// DispatcherUnhandledException and tears down the supervisor itself. Both hazards are contained
    /// below the call: <see cref="Process.GetProcessById(int)"/> throws <see cref="ArgumentException"/>
    /// if the process isn't running — swallowed by the adopt factory; <see cref="Process.MainModule"/>
    /// throws on a 64/32-bit bitness mismatch or access denied — swallowed by
    /// <c>RealProcessHandle.ExePath</c>, which yields null instead. A null <see cref="ExePath"/> is
    /// acceptable because the binder does its own exe-path authentication via
    /// <c>QueryFullProcessImageName</c>.</para>
    /// </remarks>
    public bool Adopt(int pid)
    {
        ReleasePrevious();
        KillInitiated = false;
        Bind(_adoptFactory(pid));
        if (_process != null)
        {
            ExePath = _process.ExePath;
            StartTimeUtc = _process.StartTime;
        }
        return _process != null;
    }

    public bool IsAlive => _process is { HasExited: false };

    public void ForceKill()
    {
        // Set BEFORE the HasExited check: recovery is an INTENT, not "did the kill actually execute."
        // If the process exits on its own between stale-detection and this call, HasExited is already
        // true and the kill is skipped — but the late Process.Exited (marshaled via BeginInvoke, so it
        // runs after this synchronous block) would otherwise double-count. KillInitiated covers that.
        KillInitiated = true;
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                // The kill can throw (the process raced natural exit between the HasExited check
                // and Kill, or access was denied). ForceKill runs on the supervisor's dispatcher,
                // which has no other handler — the WATCHDOG would die exactly when it is supposed
                // to be recovering the app. Raced natural exit still feeds ProcessExited via the
                // late Process.Exited; ACCESS-DENIED does not (no Exited will arrive, and
                // KillInitiated stays set until the next Launch/Adopt clears it, suppressing the
                // next strike) — unreachable for a same-user child in practice.
                Serilog.Log.Error(ex, "ForceKill: Process.Kill threw (raced natural exit or access denied).");
            }
        }
    }

    /// <summary>Binds a handle and wires its exit to <see cref="BoundProcessExited"/>.</summary>
    private void Bind(IProcessHandle? handle)
    {
        _process = handle;
        if (handle is null) return;
        _onExited = () => BoundProcessExited?.Invoke(handle);
        handle.Exited += _onExited;
    }

    /// <summary>
    /// Releases the bound process without killing it (ticket 06-B stand-down). The exit handler is
    /// detached, so a later exit of the released process books nothing. Idempotent.
    /// </summary>
    public void Unbind()
    {
        ReleasePrevious();
        KillInitiated = false;
    }

    /// <summary>Detaches the exit handler and disposes the previously-bound handle before a relaunch/adopt.</summary>
    private void ReleasePrevious()
    {
        if (_process is not null && _onExited is not null)
        {
            try { _process.Exited -= _onExited; } catch { }
        }
        _onExited = null;
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}
