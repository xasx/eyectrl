namespace EyeCtrl.Platform.Interop;

using System.IO.Pipes;

/// <summary>
/// Client-side identity of the PROCESS SERVING a named pipe the client just connected to.
/// <para>
/// Pipe names are machine-global: any local process can create a server for a known name and
/// impersonate the real service (name squatting). Asking the kernel who serves the connected
/// instance is the one check a squatter cannot fake — the kernel reports the creator of the
/// first pipe instance, not anything the peer claims on the wire.
/// </para>
/// <para>
/// Every method fails closed (returns false) on any error and never throws: callers use these
/// to GATE a channel, so a throw would trade an impersonation risk for a crash risk.
/// </para>
/// </summary>
public static class PipeServerIdentity
{
    /// <summary>The process id of the server behind the pipe instance this client is connected to.</summary>
    public static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint serverProcessId)
    {
        serverProcessId = 0;
        try
        {
            return ProcessIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out serverProcessId)
                   && serverProcessId != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The full exe path of the server process (via PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static bool TryGetServerExePath(NamedPipeClientStream pipe, out string? exePath)
    {
        exePath = null;
        if (!TryGetServerProcessId(pipe, out var pid))
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

    /// <summary>Whether the server process runs in the same Windows session as the caller —
    /// blocks a same-user pipe squatter in another session (fast-user-switch, remote session).</summary>
    public static bool ServerIsSameSession(NamedPipeClientStream pipe)
    {
        if (!TryGetServerProcessId(pipe, out var pid))
            return false;
        if (!ProcessIdentity.ProcessIdToSessionId(pid, out var session)
            || !ProcessIdentity.ProcessIdToSessionId((uint)Environment.ProcessId, out var own))
            return false;
        return session == own;
    }
}
