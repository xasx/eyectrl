namespace EyeCtrl.Setup.Windows;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EyeCtrl.Core.Input;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The production process seam. Enumeration reads only <c>QueryFullProcessImageName</c> with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (plan §10 item 4); starts use ShellExecute; stops and
/// kills use <c>Process.Kill(entireProcessTree: true)</c>.
/// </summary>
public sealed class WindowsProcessControl : IProcessControl
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Handles returned by <see cref="Start"/>, kept so later checks do not reopen a PID
    /// that may already have been reused. Removed when the process is seen exited, killed or waited.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> Started = new();

    /// <inheritdoc />
    public IReadOnlyList<RunningProcess> Snapshot()
    {
        var result = new List<RunningProcess>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                result.Add(new RunningProcess(process.Id, QueryImagePath(process.Id)));
            }
            catch (Exception)
            {
                result.Add(new RunningProcess(process.Id, null));
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    /// <inheritdoc />
    public int? Start(string exePath, IReadOnlyList<string>? arguments = null)
    {
        try
        {
            // ShellExecute, never CreateProcess: S1a measured Win32 740 for a CreateProcess of a
            // uiAccess exe, even from a UIAccess parent, while ShellExecute starts it and returns
            // a usable handle.
            var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = true };
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                    startInfo.ArgumentList.Add(argument);
            }

            var started = Process.Start(startInfo);
            if (started is null)
                return null;

            var pid = started.Id;
            Started[pid] = started;
            return pid;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Stop(int processId) => Kill(processId);

    /// <inheritdoc />
    public void Kill(int processId)
    {
        try
        {
            // The process itself only. Never Kill(entireProcessTree: true): the App's children
            // include the owner's Quick Action programs, and the Supervisor's include TD Control
            // or Windows Eye Control when FallbackAt started them.
            var process = Take(processId);
            try
            {
                process.Kill();
            }
            finally
            {
                process.Dispose();
            }
        }
        catch (Exception)
        {
            // Already gone is the outcome we wanted; access denied is left to the image check,
            // which then refuses the swap instead of pretending it stopped.
        }
    }

    /// <inheritdoc />
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            var process = Take(processId);
            try
            {
                var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
                if (!exited)
                    Started[processId] = process;
                return exited;
            }
            finally
            {
                if (!Started.ContainsKey(processId))
                    process.Dispose();
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsAlive(int processId)
    {
        try
        {
            var process = Take(processId);
            try
            {
                var alive = !process.HasExited;
                if (alive)
                    Started[processId] = process;
                return alive;
            }
            finally
            {
                if (!Started.ContainsKey(processId))
                    process.Dispose();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Process Take(int processId)
    {
        if (Started.TryRemove(processId, out var cached))
            return cached;

        return Process.GetProcessById(processId);
    }

    /// <inheritdoc />
    public ProcessTokenFacts ReadToken(int processId) => WindowsTokenReader.Read(processId);

    internal static string? QueryImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>The token read behind the pass rule: integrity RID, elevation, UIAccess. Only
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and <c>TOKEN_QUERY</c> are requested, exactly the rights
/// the plan measured as available to a medium process on a High/UIAccess App.</summary>
internal static class WindowsTokenReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int TokenUIAccess = 26;
    private const int TokenElevation = 20;
    private const int ErrorAccessDenied = 5;

    public static ProcessTokenFacts Read(int processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == ErrorAccessDenied
                ? new ProcessTokenFacts(null, null, null, null, AccessDenied: true)
                : ProcessTokenFacts.Unknown;
        }

        try
        {
            var name = Path.GetFileName(WindowsProcessControl.QueryImagePath(processId));

            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                var denied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
                return new ProcessTokenFacts(name, null, null, null, denied);
            }

            try
            {
                return new ProcessTokenFacts(
                    name,
                    ReadIntegrityLevel(tokenHandle),
                    ReadDword(tokenHandle, TokenElevation) is 1,
                    ReadDword(tokenHandle, TokenUIAccess) is 1,
                    AccessDenied: false);
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static int? ReadIntegrityLevel(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, 256, out _))
                return null;

            var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            if (label.Sid == IntPtr.Zero)
                return null;

            var count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
            if (count == 0)
                return null;

            var rid = GetSidSubAuthority(label.Sid, (uint)(count - 1));
            return rid == IntPtr.Zero ? null : Marshal.ReadInt32(rid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ReadDword(IntPtr token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            return GetTokenInformation(token, informationClass, buffer, 4, out _)
                ? Marshal.ReadInt32(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
}
