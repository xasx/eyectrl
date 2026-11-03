namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// The Win32 calls behind R1 and R2: cursor reads, hit-testing, and the token facts the integrity
/// comparison uses. Split from <see cref="NativeMethods"/> so the blocked-input diagnostics stay
/// readable and the existing input-path declarations are untouched.
/// </summary>
internal static partial class InputIntegrityNativeMethods
{
    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION.</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>TOKEN_QUERY.</summary>
    internal const uint TokenQuery = 0x0008;

    /// <summary>TokenIntegrityLevel (<c>TOKEN_INFORMATION_CLASS</c>).</summary>
    internal const int TokenIntegrityLevel = 25;

    /// <summary>TokenElevation (<c>TOKEN_INFORMATION_CLASS</c>).</summary>
    internal const int TokenElevationInfo = 20;

    /// <summary>TokenUIAccess (<c>TOKEN_INFORMATION_CLASS</c>).</summary>
    internal const int TokenUIAccessInfo = 26;

    /// <summary>ERROR_ACCESS_DENIED — the one failure that itself means "above us".</summary>
    internal const int ErrorAccessDenied = 5;

    /// <summary>GA_ROOTOWNER: the top-level owner of a window (a dialog's owning app).</summary>
    internal const uint GaRootOwner = 3;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out NativeMethods.POINT point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(NativeMethods.POINT point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    // SID layout (winnt.h): Revision (1 byte), SubAuthorityCount (1 byte),
    // IdentifierAuthority (6 bytes), then SubAuthority[SubAuthorityCount] at offset 8. The last
    // sub-authority of a mandatory label's SID is the integrity RID (0x2000 medium, 0x3000 high).
    private const int SidSubAuthorityOffset = 8;

    /// <summary>Reads the integrity RID from a TOKEN_MANDATORY_LABEL buffer.</summary>
    internal static int? ReadIntegrityLevel(IntPtr mandatoryLabel)
    {
        var sid = Marshal.ReadIntPtr(mandatoryLabel); // SID_AND_ATTRIBUTES.Sid (first field)
        if (sid == IntPtr.Zero) return null;

        int count = Marshal.ReadByte(sid, 1);
        if (count <= 0) return null;

        return Marshal.ReadInt32(sid, SidSubAuthorityOffset + (count - 1) * 4);
    }
}
