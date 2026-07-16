namespace EyeCtrl.Supervisor.Ipc;

using System;
using EyeCtrl.Platform.Interop;
using Microsoft.Win32.SafeHandles;

public sealed record ClientIdentity(uint Pid, uint SessionId, string ExePath, DateTime CreationTimeUtc);

public sealed class ClientIdentityBinder
{
    /// <summary>Expected app exe path and the launched process's creation time (from ProcessController).</summary>
    public ClientIdentityBinder(string expectedExePath, DateTime expectedCreationTimeUtc)
    {
        _expectedExePath = expectedExePath;
        _expectedCreation = expectedCreationTimeUtc;
    }

    /// <summary>null = unbound (ignore heartbeats). Any P/Invoke failure -> null, never throws.</summary>
    public ClientIdentity? Resolve(SafePipeHandle pipe)
    {
        try
        {
            if (!ProcessIdentity.GetNamedPipeClientProcessId(pipe, out uint pid) || pid == 0) return null;
            if (!ProcessIdentity.ProcessIdToSessionId(pid, out uint session)) return null;

            using var ph = ProcessIdentity.OpenProcess(ProcessIdentity.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (ph.IsInvalid) return null;

            // Exe path:
            var buf = new char[1024]; uint size = (uint)buf.Length;
            if (!ProcessIdentity.QueryFullProcessImageName(ph.DangerousGetHandle(), 0, buf, ref size)) return null;
            string exe = new string(buf, 0, (int)size);
            if (!string.Equals(exe, _expectedExePath, StringComparison.OrdinalIgnoreCase)) return null;

            // Creation time (PID-reuse guard): compare to the launched process's creation time.
            if (!ProcessIdentity.GetProcessTimes(ph.DangerousGetHandle(), out var c, out _, out _, out _)) return null;
            var creation = DateTime.FromFileTimeUtc(((long)c.HighDateTime << 32) | (uint)c.LowDateTime);
            if (Math.Abs((creation - _expectedCreation).TotalSeconds) > 2) return null;

            return new ClientIdentity(pid, session, exe, creation);
        }
        catch { return null; } // unbound on any failure — never crash the pipe server
    }

    private readonly string _expectedExePath;
    private readonly DateTime _expectedCreation;
}
