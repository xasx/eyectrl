namespace EyeCtrl.Platform.Tests.Icons;

using System.Reflection;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B's COM/interop contract, pinned as metadata against the Windows SDK headers (Windows Kits
/// 10.0.26100.0): every IID, property key, struct size and vtable order the icon lookup depends on.
/// A wrong IID makes the OS answer E_NOINTERFACE (the property store silently yields no AUMID); a
/// shifted vtable slot calls the wrong managed method (the classic access violation). No COM is
/// touched here — these are pure metadata assertions.
/// </summary>
public class IconInteropPinTests
{
    private static void PinIid<T>(string expected, string citation) =>
        Check.WithCustomMessage($"the IID of {typeof(T).Name} drifted from the SDK ({citation})")
            .That(typeof(T).GUID.ToString("D")).IsEqualTo(expected.ToLowerInvariant());

    [Fact]
    public void TheIids_AreTheSdkHeaderValues()
    {
        // propsys.h:516
        PinIid<IconNativeMethods.IPropertyStore>("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99", "propsys.h:516");
        // shobjidl_core.h:9383
        PinIid<IconNativeMethods.IShellItemImageFactory>(
            "bcc18b79-ba16-442f-80c4-8a59c30c463b", "shobjidl_core.h:9383");
    }

    [Fact]
    public void TheVtables_AreInSdkHeaderOrder()
    {
        // propsys.h:516-548 — GetCount, GetAt, GetValue, SetValue, Commit after IUnknown.
        Check.WithCustomMessage("IPropertyStore's vtable drifted from propsys.h (GetValue is slot 5 on x64)")
            .That(typeof(IconNativeMethods.IPropertyStore).GetMethods().Select(m => m.Name).ToArray())
            .ContainsExactly("GetCount", "GetAt", "GetValue", "SetValue", "Commit");

        // shobjidl_core.h:9417-9421 — one method after IUnknown.
        Check.WithCustomMessage("IShellItemImageFactory's vtable drifted from shobjidl_core.h")
            .That(typeof(IconNativeMethods.IShellItemImageFactory).GetMethods().Select(m => m.Name).ToArray())
            .ContainsExactly("GetImage");
    }

    [Fact]
    public void ThePropertyKey_IsTheSdkAppUserModelId()
    {
        // propkey.h:8605 — DEFINE_PROPERTYKEY(PKEY_AppUserModel_ID, 0x9F4C2855, 0x9F79, 0x4B39,
        // 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3, 5)
        var key = IconNativeMethods.AppUserModelIdKey;
        Check.That(key.FormatId.ToString("D")).IsEqualTo("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
        Check.That(key.PropertyId).IsEqualTo(5u);
        Check.That(Marshal.SizeOf<IconNativeMethods.PropertyKey>()).IsEqualTo(20);
    }

    [Fact]
    public void TheStructSizes_MatchTheX64Abi()
    {
        // PropIdlBase.h:302-372 — an 8-byte header plus a 16-byte union tail on x64. A short struct
        // would let PropVariantClear write past the stack slot.
        Check.That(Marshal.SizeOf<IconNativeMethods.PropVariant>()).IsEqualTo(24);

        // windef.h:186-190 — SIZE { LONG cx; LONG cy; }
        Check.That(Marshal.SizeOf<IconNativeMethods.Size>()).IsEqualTo(8);
    }

    [Fact]
    public void TheWin32Constants_AreTheHeaderValues()
    {
        // winuser.h:2134, :2639, :1968
        Check.That(IconNativeMethods.WM_GETICON).IsEqualTo(0x007Fu);
        Check.That(IconNativeMethods.ICON_BIG).IsEqualTo(1);
        Check.That(IconNativeMethods.GCLP_HICON).IsEqualTo(-14);
        // winuser.h:2614-2615
        Check.That(IconNativeMethods.SMTO_BLOCK).IsEqualTo(0x0001u);
        Check.That(IconNativeMethods.SMTO_ABORTIFHUNG).IsEqualTo(0x0002u);
        // shobjidl_core.h:9355-9358
        Check.That(IconNativeMethods.SIIGBF_RESIZETOFIT).IsEqualTo(0x0000);
        Check.That(IconNativeMethods.SIIGBF_BIGGERSIZEOK).IsEqualTo(0x0001);
        Check.That(IconNativeMethods.SIIGBF_ICONONLY).IsEqualTo(0x0004);
        // wtypes.h:865
        Check.That(IconNativeMethods.VT_LPWSTR).IsEqualTo((ushort)31);
    }
}
