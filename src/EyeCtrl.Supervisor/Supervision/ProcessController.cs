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

    public void Launch(string exePath, bool paused)
    {
        ExePath = exePath;
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        _process = Process.Start(psi);
        StartTimeUtc = _process?.StartTime.ToUniversalTime();
    }

    public bool IsAlive => _process is { HasExited: false };

    public void ForceKill()
    {
        if (_process is { HasExited: false })
            _process.Kill(entireProcessTree: true);
    }
}
