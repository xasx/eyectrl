namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Diagnostics;

public sealed class ProcessController
{
    private Process? _process;
    public Process? BoundProcess => _process;
    /// <summary>Captured at launch; the EXPECTED creation time the ClientIdentityBinder checks against.</summary>
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

    public void Launch(string exePath, bool paused)
    {
        ReleasePrevious(); // M2: dispose the prior Process handle (OS handle) before overwriting.
        KillInitiated = false;
        ExePath = exePath;
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        _process = Process.Start(psi);
        try { StartTimeUtc = _process?.StartTime.ToUniversalTime(); }
        catch (Exception) { StartTimeUtc = null; } // M1: StartTime throws if the process already exited.
    }

    /// <summary>
    /// Binds an externally-launched process (one the supervisor did NOT start) by PID, so its
    /// exit can be detected and it can be force-killed if hung. Used by the manual-launch
    /// adoption path. Returns false if the PID is not running / cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Process.GetProcessById(int)"/> throws <see cref="ArgumentException"/> if the
    /// process isn't running; <see cref="Process.MainModule"/> can throw on a 64/32-bit bitness
    /// mismatch or access denied — both are swallowed by the try/catch. <see cref="ExePath"/> is
    /// read via <c>MainModule?.FileName</c> and may be null when the module lookup fails; that's
    /// acceptable here because the binder (constructed separately in adoption mode) does its own
    /// exe-path authentication via QueryFullProcessImageName.</para>
    /// </remarks>
    public bool Adopt(int pid)
    {
        ReleasePrevious();
        KillInitiated = false;
        try
        {
            _process = Process.GetProcessById(pid);
            ExePath = _process.MainModule?.FileName;
            try { StartTimeUtc = _process.StartTime.ToUniversalTime(); }
            catch (Exception) { StartTimeUtc = null; }
            return true;
        }
        catch
        {
            _process = null;
            return false;
        }
    }

    public bool IsAlive => _process is { HasExited: false };

    public void ForceKill()
    {
        if (_process is { HasExited: false })
        {
            KillInitiated = true; // suppress the Exited-event double-count (see KillInitiated docs)
            _process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Disposes the previously-bound Process (releases its OS handle) before a relaunch/adopt.</summary>
    private void ReleasePrevious()
    {
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}
