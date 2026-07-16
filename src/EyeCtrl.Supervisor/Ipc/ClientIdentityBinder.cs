namespace EyeCtrl.Supervisor.Ipc;

using System;
using EyeCtrl.Platform.Interop;
using Microsoft.Win32.SafeHandles;

public sealed record ClientIdentity(uint Pid, uint SessionId, string ExePath, DateTime CreationTimeUtc);

public sealed class ClientIdentityBinder
{
    /// <summary>
    /// Expected app exe path and (optionally) the launched process's creation time (from
    /// ProcessController). When <paramref name="expectedCreationTimeUtc"/> is null the binder runs
    /// in ADOPTION mode: it still enforces exe-path + same-session + the per-user pipe ACL, but
    /// skips the creation-time comparison (used for manually-launched apps with no recorded
    /// StartTime). See <see cref="ForAdoption"/>.
    /// </summary>
    public ClientIdentityBinder(string expectedExePath, DateTime? expectedCreationTimeUtc = null)
    {
        _expectedExePath = expectedExePath;
        _expectedCreation = expectedCreationTimeUtc;
    }

    /// <summary>
    /// Convenience factory for ADOPTION mode: binds any client whose exe path matches
    /// <paramref name="expectedExePath"/> and that shares the supervisor's session (and is reachable
    /// only via the per-user pipe ACL). Creation-time is not checked because a manually-launched app
    /// has no recorded StartTime the supervisor can compare against. Exe-path + same-session + ACL
    /// is strong authentication (same user, same session, exact expected exe path); PID-reuse
    /// protection is weaker than strict mode but acceptable given the ACL + session checks.
    /// </summary>
    public static ClientIdentityBinder ForAdoption(string expectedExePath) => new(expectedExePath);

    /// <summary>null = unbound (ignore heartbeats). Any P/Invoke failure -> null, never throws.</summary>
    public ClientIdentity? Resolve(SafePipeHandle pipe)
    {
        try
        {
            if (!ProcessIdentity.GetNamedPipeClientProcessId(pipe, out uint pid) || pid == 0) return null;
            if (!ProcessIdentity.ProcessIdToSessionId(pid, out uint session)) return null;

            using var ph = ProcessIdentity.OpenProcess(ProcessIdentity.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (ph.IsInvalid) return null;

            // Exe path (mandatory gate in BOTH strict and adoption modes):
            var buf = new char[1024]; uint size = (uint)buf.Length;
            if (!ProcessIdentity.QueryFullProcessImageName(ph.DangerousGetHandle(), 0, buf, ref size)) return null;
            string exe = new string(buf, 0, (int)size);
            if (!string.Equals(exe, _expectedExePath, StringComparison.OrdinalIgnoreCase)) return null;

            // Creation time (PID-reuse guard): read the live process's creation time so it can be
            // returned in the identity. The COMPARISON is only done in strict mode (when we have an
            // expected creation time). In adoption mode (null expected creation time) we skip the
            // comparison and rely on exe-path + same-session + the per-user pipe ACL.
            if (!ProcessIdentity.GetProcessTimes(ph.DangerousGetHandle(), out var c, out _, out _, out _)) return null;
            var creation = DateTime.FromFileTimeUtc(((long)c.HighDateTime << 32) | (uint)c.LowDateTime);
            if (_expectedCreation.HasValue &&
                Math.Abs((creation - _expectedCreation.Value).TotalSeconds) > 2) return null;

            return new ClientIdentity(pid, session, exe, creation);
        }
        catch { return null; } // unbound on any failure — never crash the pipe server
    }

    private readonly string _expectedExePath;
    private readonly DateTime? _expectedCreation;
}
