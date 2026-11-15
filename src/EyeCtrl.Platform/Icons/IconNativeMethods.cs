namespace EyeCtrl.Platform.Icons;

using System.Runtime.InteropServices;

/// <summary>
/// Win32/COM declarations for the Task Switcher's icon sources (S3-B). Every IID, CLSID, property
/// key, constant and vtable order below was checked against the Windows SDK headers
/// (Windows Kits 10.0.26100.0) and names its header line — a wrong IID once presented as "Windows
/// refuses" and cost a diagnosis round, so the citations are part of the contract.
///
/// <para>
/// The <c>[LibraryImport]</c> half is resolved to real exports up front by
/// <c>NativeMethodEntryPointTests.EveryLibraryImport_ResolvesToARealExport</c> (which includes this
/// class). The COM half stays <c>[DllImport]</c> like the shell declarations in
/// <see cref="EyeCtrl.Platform.Services.Win32InstalledAppProvider"/>: <c>[LibraryImport]</c> cannot
/// marshal a COM interface out-parameter.
/// </para>
/// </summary>
internal static partial class IconNativeMethods
{
    // ── user32: the window's own icon ───────────────────────────────────────────────────────

    /// <summary>winuser.h:2134 — <c>#define WM_GETICON 0x007F</c>.</summary>
    internal const uint WM_GETICON = 0x007F;

    /// <summary>winuser.h:2639 — <c>#define ICON_BIG 1</c>.</summary>
    internal const int ICON_BIG = 1;

    /// <summary>winuser.h:1968 — <c>#define GCLP_HICON (-14)</c>.</summary>
    internal const int GCLP_HICON = -14;

    /// <summary>winuser.h:2614 — <c>#define SMTO_BLOCK 0x0001</c>.</summary>
    internal const uint SMTO_BLOCK = 0x0001;

    /// <summary>winuser.h:2615 — <c>#define SMTO_ABORTIFHUNG 0x0002</c>.</summary>
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// winuser.h:3766-3773 —
    /// <c>LRESULT SendMessageTimeoutW(HWND, UINT, WPARAM, LPARAM, UINT fuFlags, UINT uTimeout, PDWORD_PTR)</c>.
    /// Returns 0 when the target did not answer within <c>uTimeout</c> (or was already hung and
    /// <see cref="SMTO_ABORTIFHUNG"/> is set). That bound is the whole reason this call is used
    /// instead of <c>SendMessage</c>: the icon lookup runs on the UI thread, which is also the
    /// gaze-dwell thread, so a hung application must never be able to freeze control.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout,
        out IntPtr lpdwResult);

    /// <summary>
    /// winuser.h:10135 — <c>GetClassLongPtrW(HWND, int)</c>. The <c>Ptr</c> form exists only on
    /// 64-bit user32 (winuser.h:10167 maps it to <c>GetClassLongW</c> on 32-bit), and the app and
    /// its test host are win-x64.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    internal static partial IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    // ── shell: the window's AppUserModelID (Store/UWP apps) ─────────────────────────────────

    /// <summary>
    /// shellapi.h:1014 — <c>SHGetPropertyStoreForWindow(HWND, REFIID, void**)</c>. Gives the
    /// window's property bag, whose <c>System.AppUserModel.ID</c> names the packaged app hosted in
    /// an <c>ApplicationFrameWindow</c> instead of the frame host process.
    /// </summary>
    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    /// <summary>
    /// propkey.h:8605 —
    /// <c>PKEY_AppUserModel_ID</c> = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5 (VT_LPWSTR).
    /// </summary>
    internal static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>
    /// propsys.h:516-548 — <c>IPropertyStore</c>; after IUnknown the vtable is exactly
    /// GetCount, GetAt, GetValue, SetValue, Commit. A shifted slot calls the wrong managed method.
    /// </summary>
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PropertyKey pkey);

        [PreserveSig]
        int GetValue(in PropertyKey key, out PropVariant pv);

        [PreserveSig]
        int SetValue(in PropertyKey key, in PropVariant pv);

        [PreserveSig]
        int Commit();
    }

    /// <summary>
    /// propkey.h's <c>PROPERTYKEY</c> — { GUID fmtid; DWORD pid; }, 20 bytes, 4-byte aligned.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>
    /// PropIdlBase.h:302-372 — <c>tagPROPVARIANT</c>: an 8-byte header (vt + three reserved words)
    /// followed by a union whose largest x64 member is 16 bytes (CAC / DECIMAL), i.e. 24 bytes.
    /// Declared at its real size so <c>PropVariantClear</c> can never write past the stack slot.
    /// Only the VT_LPWSTR arm is read.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(2)] public ushort Reserved1;
        [FieldOffset(4)] public ushort Reserved2;
        [FieldOffset(6)] public ushort Reserved3;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    /// <summary>wtypes.h:865 — <c>VT_LPWSTR = 31</c>.</summary>
    internal const ushort VT_LPWSTR = 31;

    /// <summary>
    /// PropIdl.h:1219 / combaseapi.h:1241 — <c>WINOLEAPI PropVariantClear(PROPVARIANT*)</c>, exported
    /// by ole32. Every PROPVARIANT returned by <see cref="IPropertyStore.GetValue"/> must be cleared
    /// or its string leaks.
    /// </summary>
    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant pvar);

    // ── shell: large shell images (exe icon / packaged app icon) ────────────────────────────

    /// <summary>
    /// shobjidl_core.h:9006 — <c>SHCreateItemFromParsingName(PCWSTR, IBindCtx*, REFIID, void**)</c>.
    /// Used with a filesystem path (the exe) or <c>shell:AppsFolder\{AUMID}</c> (a packaged app).
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    /// <summary>
    /// shobjidl_core.h:9383-9457 — <c>IID_IShellItemImageFactory</c>
    /// {bcc18b79-ba16-442f-80c4-8a59c30c463b}; one method after IUnknown
    /// (vtable 9417-9421): <c>GetImage(SIZE, SIIGBF, HBITMAP*)</c>.
    /// </summary>
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, int flags, out IntPtr phbm);
    }

    /// <summary>windef.h:186-190 — <c>SIZE { LONG cx; LONG cy; }</c>, passed by value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        public int Width;
        public int Height;
    }

    /// <summary>shobjidl_core.h:9355 — <c>SIIGBF_RESIZETOFIT = 0</c>.</summary>
    internal const int SIIGBF_RESIZETOFIT = 0x0000;

    /// <summary>shobjidl_core.h:9356 — <c>SIIGBF_BIGGERSIZEOK = 0x1</c> (prefer a larger source).</summary>
    internal const int SIIGBF_BIGGERSIZEOK = 0x0001;

    /// <summary>
    /// shobjidl_core.h:9358 — <c>SIIGBF_ICONONLY = 0x4</c>: the item's ICON, never a thumbnail
    /// (an exe has no thumbnail provider worth a placeholder frame).
    /// </summary>
    internal const int SIIGBF_ICONONLY = 0x0004;

    /// <summary>
    /// wingdi.h:3951 — <c>BOOL DeleteObject(HGDIOBJ)</c>. <c>GetImage</c> hands over an HBITMAP
    /// that the caller owns; it must be deleted after it has been copied into a WPF bitmap.
    /// </summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);
}
