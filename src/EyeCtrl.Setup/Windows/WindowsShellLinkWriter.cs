namespace EyeCtrl.Setup.Windows;

using System.Runtime.InteropServices;
using System.Text;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Writes real .lnk shell shortcuts through the shell COM API (IShellLinkW + IPersistFile). The
/// interop follows the proven <c>CoreAudioInterop</c> pattern: a ComImport coclass activated by
/// plain <c>new</c> (the cast hops through object — a direct coclass-to-interface cast is CS0030
/// on modern .NET, at runtime the hop is the RCW's QI), vtable-exact <c>[PreserveSig]</c>
/// interfaces, and every slot declared even when unused, because the vtable must line up.
/// The vtable order below is copied from the SDK on disk (10.0.26100.0), where the pairs run
/// Get-first (GetDescription 4th, SetPath LAST at 18) — the widely copied community interop
/// orderings get this wrong, so slot positions were verified against
/// <c>ShObjIdl_core.h</c>/<c>ShObjIdl_core.idl</c> before shipping.
/// </summary>
public sealed class WindowsShellLinkWriter : IShortcutFileWriter
{
    /// <summary>Creates or overwrites one .lnk whose target (and icon) is the given exe.</summary>
    public void Write(string linkPath, string targetExePath)
    {
        var link = (IShellLinkW)(object)new ShellLinkComObject();
        try
        {
            ThrowIfFailed(link.SetPath(targetExePath));
            ThrowIfFailed(link.SetIconLocation(targetExePath, 0));
            ThrowIfFailed(link.SetDescription("EyeCtrl"));
            ThrowIfFailed(link.SetWorkingDirectory(Path.GetDirectoryName(targetExePath) ?? string.Empty));

            // IPersistFile::Save(fRemember: true) materialises the .lnk on disk.
            var persist = (IPersistFile)link;
            ThrowIfFailed(persist.Save(linkPath, true));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static void ThrowIfFailed(int hresult) =>
        Marshal.ThrowExceptionForHR(hresult);

    /// <summary>The ShellLink coclass (ShObjIdl_core.idl:8272, uuid 00021401-…), activated by plain new.</summary>
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkComObject
    {
    }

    /// <summary>SDK ShObjIdl_core.h:12654 (IID 000214F9-… at :12649, SDK 10.0.26100.0) — the
    /// eighteen slots in header order; the unused ones stay declared.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr findData, int flags);

        [PreserveSig] int GetIDList(out IntPtr pidl);

        [PreserveSig] int SetIDList(IntPtr pidl);

        [PreserveSig] int GetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig] int GetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        [PreserveSig] int GetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        [PreserveSig] int GetHotkey(out ushort hotkey);

        [PreserveSig] int SetHotkey(ushort hotkey);

        [PreserveSig] int GetShowCmd(out int showCmd);

        [PreserveSig] int SetShowCmd(int showCmd);

        [PreserveSig] int GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int iconIndex);

        [PreserveSig] int SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iconIndex);

        [PreserveSig] int SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string pszRelativePath, int reserved);

        [PreserveSig] int Resolve(IntPtr hwnd, int flags);

        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>SDK objidl.h:11727 (IID 0000010B-… at :11722), after IPersist's GetClassID slot.</summary>
    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);

        [PreserveSig] int IsDirty();

        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int mode);

        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
