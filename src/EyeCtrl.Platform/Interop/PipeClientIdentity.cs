namespace EyeCtrl.Platform.Interop;

using System.IO.Pipes;

/// <summary>
/// Server-side identity of the PROCESS CONNECTED to a named pipe instance — the mirror of
/// <see cref="PipeServerIdentity"/>. A pipe ACL restricts a channel to the current user, but every
/// process of that user can still connect; a channel that changes the App's settings must also
/// know WHICH process is asking. The kernel reports the client's process id, which the peer
/// cannot fake on the wire.
/// <para>
/// Every method fails closed (returns false) on any error and never throws: callers use these to
/// GATE a channel, so a throw would trade an impersonation risk for a crash risk.
/// </para>
/// </summary>
public static class PipeClientIdentity
{
    /// <summary>The process id of the client connected to this server pipe instance.</summary>
    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint clientProcessId)
    {
        clientProcessId = 0;
        try
        {
            return ProcessIdentity.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out clientProcessId)
                   && clientProcessId != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The full exe path of the client process (via PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static bool TryGetClientExePath(NamedPipeServerStream pipe, out string? exePath)
    {
        exePath = null;
        try
        {
            if (!TryGetClientProcessId(pipe, out var pid))
                return false;
            using var ph = ProcessIdentity.OpenProcess(
                ProcessIdentity.PROCESS_QUERY_LIMITED_INFORMATION, inherit: false, pid);
            if (ph.IsInvalid)
                return false;
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            if (!ProcessIdentity.QueryFullProcessImageName(ph.DangerousGetHandle(), 0, buf, ref size))
                return false;
            exePath = new string(buf, 0, (int)size);
            return true;
        }
        catch
        {
            exePath = null;
            return false;
        }
    }

    /// <summary>Whether the client process runs in the same Windows session as the caller —
    /// blocks a same-user client in another session (fast-user-switch, remote session).</summary>
    public static bool ClientIsSameSession(NamedPipeServerStream pipe)
    {
        try
        {
            if (!TryGetClientProcessId(pipe, out var pid))
                return false;
            if (!ProcessIdentity.ProcessIdToSessionId(pid, out var session)
                || !ProcessIdentity.ProcessIdToSessionId((uint)Environment.ProcessId, out var own))
                return false;
            return session == own;
        }
        catch
        {
            return false;
        }
    }
}
