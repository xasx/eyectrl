namespace EyeCtrl.Platform.Tests.Thumbnails;

using System.Reflection;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Thumbnails;

/// <summary>
/// S4-C's DWM declarations, pinned against <c>dwmapi.h</c>'s own layout. The repo lesson is that a
/// struct or flag typo looks exactly like "Windows refuses": every call returns a non-zero HRESULT
/// and the switcher silently falls back to icon tiles. These tests read the managed layout, not a
/// live call, so they run headless and register nothing.
///
/// <para>
/// Header: <c>C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\dwmapi.h</c> —
/// <c>DWM_THUMBNAIL_PROPERTIES</c> (line 146) = <c>DWORD dwFlags; RECT rcDestination; RECT
/// rcSource; BYTE opacity; BOOL fVisible; BOOL fSourceClientAreaOnly;</c>. The header includes
/// <c>pshpack1.h</c> at line 24 and its <c>poppack.h</c> only at line 696, so the struct is
/// <b>Pack = 1</b>: <b>45 bytes</b>, <c>opacity</c> at 36, <c>fVisible</c> at <b>37</b> and
/// <c>fSourceClientAreaOnly</c> at <b>41</b> — no padding after the BYTE. (The review of
/// <c>f0568249</c> caught the default-packed 48-byte declaration; it worked only because the
/// padding zeros made DWM read TRUE/FALSE by accident.) <c>HTHUMBNAIL</c> = <c>HANDLE</c>; the
/// <c>DWM_TNP_*</c> flags are 0x1, 0x2, 0x4, 0x8, 0x10.
/// </para>
/// </summary>
public class DwmThumbnailLayoutTests
{
    [Fact]
    public void ThumbnailProperties_MatchThePackedDwmapiHeaderLayout()
    {
        // pshpack1.h: the native struct is 45 bytes, not the default-packed 48.
        Check.That(Marshal.SizeOf<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>()).IsEqualTo(45);

        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.dwFlags))).IsEqualTo(0);
        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.rcDestination))).IsEqualTo(4);
        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.rcSource))).IsEqualTo(20);
        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.opacity))).IsEqualTo(36);
        // Pack = 1: the first BOOL follows the BYTE immediately, with no padding.
        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.fVisible))).IsEqualTo(37);
        Check.That(Offset<DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES>(
            nameof(DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES.fSourceClientAreaOnly))).IsEqualTo(41);

        // Every other struct declared here is all-4-byte fields, so packing cannot move it.
        Check.That(Marshal.SizeOf<DwmThumbnailNative.RECT>()).IsEqualTo(16);
        Check.That(Marshal.SizeOf<DwmThumbnailNative.SIZE>()).IsEqualTo(8);
    }

    [Fact]
    public void TheSwitcherPropertySet_IsTheTrialsFourFlags_AtFullOpacity()
    {
        // dwmapi.h: RECTDESTINATION 0x1, RECTSOURCE 0x2, OPACITY 0x4, VISIBLE 0x8,
        // SOURCECLIENTAREAONLY 0x10.
        Check.That(DwmThumbnailNative.DwmTnpRectDestination).IsEqualTo(0x1);
        Check.That(DwmThumbnailNative.DwmTnpOpacity).IsEqualTo(0x4);
        Check.That(DwmThumbnailNative.DwmTnpVisible).IsEqualTo(0x8);
        Check.That(DwmThumbnailNative.DwmTnpSourceClientAreaOnly).IsEqualTo(0x10);

        // RECTDESTINATION | VISIBLE | OPACITY | SOURCECLIENTAREAONLY — and NOT RECTSOURCE (the
        // whole source window is the picture).
        Check.That(DwmThumbnailNative.SwitcherPropertyFlags).IsEqualTo(0x1D);
        Check.That((DwmThumbnailNative.SwitcherPropertyFlags & 0x2)).IsEqualTo(0);
        Check.That(DwmThumbnailNative.Opaque).IsEqualTo((byte)255);
    }

    [Fact]
    public void EveryThumbnailEntryPoint_IsDeclaredOnDwmapiDll_WithTheHeadersName()
    {
        var expected = new Dictionary<string, string>
        {
            [nameof(DwmThumbnailNative.DwmRegisterThumbnail)] = "DwmRegisterThumbnail",
            [nameof(DwmThumbnailNative.DwmUpdateThumbnailProperties)] = "DwmUpdateThumbnailProperties",
            [nameof(DwmThumbnailNative.DwmQueryThumbnailSourceSize)] = "DwmQueryThumbnailSourceSize",
            [nameof(DwmThumbnailNative.DwmUnregisterThumbnail)] = "DwmUnregisterThumbnail",
        };

        foreach (var (methodName, entryPoint) in expected)
        {
            var method = typeof(DwmThumbnailNative).GetMethod(
                methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Check.WithCustomMessage($"{methodName} is missing").That(method).IsNotNull();

            var import = method!.GetCustomAttribute<DllImportAttribute>();
            Check.WithCustomMessage($"{methodName} is not a DllImport").That(import).IsNotNull();
            Check.That(import!.Value).IsEqualTo("dwmapi.dll");
            Check.That(method.Name).IsEqualTo(entryPoint);
        }
    }

    private static int Offset<T>(string field) =>
        Marshal.OffsetOf<T>(field).ToInt32();
}
