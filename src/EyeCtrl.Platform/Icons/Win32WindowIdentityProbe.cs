namespace EyeCtrl.Platform.Icons;

using System.Diagnostics;
using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;
using EyeCtrl.Platform.Interop;

/// <summary>
/// The Win32/COM reads behind <see cref="IWindowIdentityProbe"/>. Every method is read-only, bounded
/// and non-throwing: a window that vanished, an inaccessible process or a window without a property
/// store all simply return null, and the icon decision falls through to its next source.
/// </summary>
internal sealed class Win32WindowIdentityProbe : IWindowIdentityProbe
{
    /// <summary>
    /// Buffer for <c>QueryFullProcessImageNameW</c>. 1024 characters covers every real install path
    /// (MAX_PATH is 260; long-path installs stay far below 1024); a longer path fails here and the
    /// decision falls through to the window's own icon rather than truncating a path.
    /// </summary>
    private const int ImagePathBufferChars = 1024;

    public string? TryGetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        try
        {
            // GetClassName's documented maximum is 256 characters; the return is the length copied.
            var buffer = new char[256];
            var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : null;
        }
        catch
        {
            return null;
        }
    }

    public string? TryGetExecutablePath(uint processId)
    {
        if (processId == 0)
            return null;

        try
        {
            // PROCESS_QUERY_LIMITED_INFORMATION, not PROCESS_QUERY_INFORMATION: Task Manager and
            // other elevated/other-user processes grant the limited right, and the path is all the
            // icon lookup needs — no module enumeration, no injection.
            using var process = ProcessIdentity.OpenProcess(
                ProcessIdentity.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process.IsInvalid)
                return null;

            var buffer = new char[ImagePathBufferChars];
            var size = (uint)buffer.Length;
            if (!ProcessIdentity.QueryFullProcessImageName(process.DangerousGetHandle(), 0, buffer, ref size)
                || size == 0)
                return null;

            return new string(buffer, 0, (int)size);
        }
        catch
        {
            return null;
        }
    }

    public string? TryGetAppUserModelId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        object? storeObject = null;
        try
        {
            var iid = typeof(IconNativeMethods.IPropertyStore).GUID;
            if (IconNativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out storeObject) != 0)
                return null;
            if (storeObject is not IconNativeMethods.IPropertyStore store)
                return null;

            var value = default(IconNativeMethods.PropVariant);
            if (store.GetValue(IconNativeMethods.AppUserModelIdKey, out value) != 0)
                return null;

            try
            {
                // The property is documented as VT_LPWSTR (propkey.h:8603). Anything else is not an
                // identity we can hand to the shell.
                return value.Vt == IconNativeMethods.VT_LPWSTR && value.PointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                // Every PROPVARIANT owns its string; without the clear it leaks on every open.
                _ = IconNativeMethods.PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (storeObject is not null)
                _ = Marshal.ReleaseComObject(storeObject);
        }
    }
}
