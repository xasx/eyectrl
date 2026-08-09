namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Diagnostics;

public sealed class ProcessController
{
    private IProcessHandle? _process;
    public IProcessHandle? BoundProcess => _process;

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

    private readonly Func<ProcessStartInfo, IProcessHandle> _processFactory;
    private readonly Func<int, IProcessHandle?> _adoptFactory;

    public ProcessController()
        : this(psi => new RealProcessHandle(Process.Start(psi)!),
               pid =>
               {
                   try { return new RealProcessHandle(Process.GetProcessById(pid)); }
                   catch { return null; }
               })
    {
    }

    public ProcessController(Func<ProcessStartInfo, IProcessHandle> processFactory, Func<int, IProcessHandle?> adoptFactory)
    {
        _processFactory = processFactory;
        _adoptFactory = adoptFactory;
    }

    public void Launch(string exePath, bool paused)
    {
        ReleasePrevious();
        KillInitiated = false;
        ExePath = exePath;
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        _process = _processFactory(psi);
    }

    /// <summary>
    /// Binds an externally-launched process by PID. Returns false if the PID is not running / cannot be opened.
    /// </summary>
    public bool Adopt(int pid)
    {
        ReleasePrevious();
        KillInitiated = false;
        _process = _adoptFactory(pid);
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
            _process.Kill(entireProcessTree: true);
    }

    /// <summary>Disposes the previously-bound handle before a relaunch/adopt.</summary>
    private void ReleasePrevious()
    {
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}
