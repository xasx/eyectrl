namespace EyeCtrl.Platform.Interop;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class ProcessIdentity
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    // [Out] char[] — NOT StringBuilder (SYSLIB1051). See NativeMethods.cs:102.
    // LibraryImport defaults to exact spelling (no A/W suffix appended), so the W entry point must be named explicitly.
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        [Out] char[] lpExeName, ref uint lpdwSize);

    // Creation-time check: open the client PID's handle and read its CreationTime via GetProcessTimes,
    // NOT a StartTime stored at launch — a crashed PID can be reused by an unrelated process with the same PID.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime { public uint LowDateTime; public uint HighDateTime; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(IntPtr hProcess,
        out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}
