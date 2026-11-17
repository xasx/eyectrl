namespace EyeCtrl.Platform.Thumbnails;

using System.Runtime.InteropServices;

/// <summary>
/// The <c>dwmapi.dll</c> thumbnail entry points and their structs. Declarations checked against
/// <c>C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\dwmapi.h</c>:
///
/// <list type="bullet">
/// <item><c>DwmRegisterThumbnail(HWND hwndDestination, HWND hwndSource, PHTHUMBNAIL phThumbnailId)</c></item>
/// <item><c>DwmUpdateThumbnailProperties(HTHUMBNAIL hThumbnailId, const DWM_THUMBNAIL_PROPERTIES* ptnProperties)</c></item>
/// <item><c>DwmQueryThumbnailSourceSize(HTHUMBNAIL hThumbnailId, PSIZE pSize)</c></item>
/// <item><c>DwmUnregisterThumbnail(HTHUMBNAIL hThumbnailId)</c></item>
/// </list>
///
/// <para>
/// <c>typedef HANDLE HTHUMBNAIL</c>, so a thumbnail id is an <see cref="IntPtr"/>. The properties
/// struct in the header is, in order, <c>DWORD dwFlags; RECT rcDestination; RECT rcSource; BYTE
/// opacity; BOOL fVisible; BOOL fSourceClientAreaOnly;</c> — 48 bytes with the 3 bytes of padding
/// after <c>opacity</c> that the native layout requires (pinned by
/// <c>DwmThumbnailLayoutTests</c>). <c>fVisible</c>/<c>fSourceClientAreaOnly</c> are 4-byte BOOLs,
/// never C# <c>bool</c> without <c>UnmanagedType.Bool</c>.
/// </para>
/// </summary>
internal static class DwmThumbnailNative
{
    // Flags for DWM_THUMBNAIL_PROPERTIES.dwFlags, dwmapi.h:
    //   #define DWM_TNP_RECTDESTINATION      0x00000001
    //   #define DWM_TNP_RECTSOURCE           0x00000002
    //   #define DWM_TNP_OPACITY              0x00000004
    //   #define DWM_TNP_VISIBLE              0x00000008
    //   #define DWM_TNP_SOURCECLIENTAREAONLY 0x00000010
    internal const int DwmTnpRectDestination = 0x00000001;
    internal const int DwmTnpOpacity = 0x00000004;
    internal const int DwmTnpVisible = 0x00000008;
    internal const int DwmTnpSourceClientAreaOnly = 0x00000010;

    /// <summary>
    /// The switcher's one property set (S4-C): destination rect + visible + opacity +
    /// whole-window source (the trial's set). <c>rcSource</c> is not specified — DWM uses the whole
    /// source window — and <c>fSourceClientAreaOnly = false</c> keeps the title bar in the picture.
    /// </summary>
    internal const int SwitcherPropertyFlags =
        DwmTnpRectDestination | DwmTnpVisible | DwmTnpOpacity | DwmTnpSourceClientAreaOnly;

    /// <summary>Fully opaque; dwmapi.h documents 0 as transparent and 255 as opaque.</summary>
    internal const byte Opaque = 255;

    /// <summary><c>RECT</c> from windef.h: four <c>LONG</c>s, inclusive-exclusive corners.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary><c>SIZE</c> from windef.h: two <c>LONG</c>s.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    /// <summary><c>DWM_THUMBNAIL_PROPERTIES</c> from dwmapi.h (sequential, no packing override).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmRegisterThumbnail(
        IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUpdateThumbnailProperties(
        IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
}
