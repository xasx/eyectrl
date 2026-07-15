namespace EyeCtrl.Platform.Services;

using System.Runtime.InteropServices;
using System.Threading;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Enumerates launchable apps from the Shell "AppsFolder" via Shell32 COM, keeping packaged apps
/// (AUMID contains '!') that <c>IApplicationActivationManager.ActivateApplication</c> can launch.
/// The walk runs on a dedicated STA thread (Shell COM requires STA) and is cached once per process;
/// any failure yields an empty list (never a thrown or cached exception).
/// </summary>
public sealed class Win32InstalledAppProvider : IInstalledAppProvider
{
    private readonly Lazy<IReadOnlyList<InstalledApp>> _cache =
        new(EnumerateOnStaThread, LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<InstalledApp> GetInstalledApps() => _cache.Value;

    private static IReadOnlyList<InstalledApp> EnumerateOnStaThread()
    {
        IReadOnlyList<InstalledApp> result = Array.Empty<InstalledApp>();
        var t = new Thread(() =>
        {
            try { result = Enumerate(); }
            catch { result = Array.Empty<InstalledApp>(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static IReadOnlyList<InstalledApp> Enumerate()
    {
        var apps = new List<InstalledApp>();

        var shellItemIid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref shellItemIid, out var folderObj) != 0
            || folderObj is not IShellItem folder)
            return apps;

        try
        {
            var bhid = BHID_EnumItems;
            var enumIid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumObj) != 0
                || enumObj is not IEnumShellItems items)
                return apps;

            try
            {
                var buffer = new IShellItem[1];
                while (items.Next(1, buffer, out var fetched) == 0 && fetched == 1)
                {
                    var item = buffer[0];
                    try
                    {
                        var name = GetDisplayName(item, SIGDN.NORMALDISPLAY);
                        var aumid = GetDisplayName(item, SIGDN.PARENTRELATIVEPARSING);
                        if (name.Length > 0 && aumid.Contains('!'))
                            apps.Add(new InstalledApp(name, aumid));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }

        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string GetDisplayName(IShellItem item, SIGDN sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0 || ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    // ── COM interop ──
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt,
            out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }
}
