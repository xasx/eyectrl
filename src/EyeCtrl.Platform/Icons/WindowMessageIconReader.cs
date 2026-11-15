namespace EyeCtrl.Platform.Icons;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// <see cref="IWindowIconReader"/> over <c>WM_GETICON(ICON_BIG)</c> with <c>SendMessageTimeout</c>,
/// then <c>GetClassLongPtr(GCLP_HICON)</c>.
///
/// <para>
/// <b>Time bound.</b> The icon lookup runs on the UI thread, which is also the gaze-dwell thread
/// (S3-A's lesson: a cross-process call that waits on a hung app takes the owner's control away).
/// <c>SendMessageTimeout</c> with <see cref="TimeoutMs"/> and <c>SMTO_ABORTIFHUNG</c> is the bound:
/// an already-hung window answers immediately (0), a briefly busy one costs at most
/// <see cref="TimeoutMs"/> — 100 ms — and then the source declines. The per-open worst case is
/// therefore 100 ms × the windows that reached this source (the exe path source precedes it and
/// covers every ordinary app).
/// </para>
///
/// <para>
/// The returned HICON is owned by the target window's class/process: it is COPIED into a WPF bitmap
/// and never passed to <c>DestroyIcon</c> — destroying a shared icon handle is the classic way to
/// break the owning app's icon.
/// </para>
/// </summary>
internal sealed class WindowMessageIconReader : IWindowIconReader
{
    /// <summary>The per-window bound in milliseconds (see the class remarks).</summary>
    internal const uint TimeoutMs = 100;

    public ImageSource? TryReadIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        try
        {
            // SMTO_BLOCK keeps our own thread from re-entering the dispatcher while it waits (bounded
            // by TimeoutMs); ABORTIFHUNG returns at once when the target is already hung.
            var icon = IconNativeMethods.SendMessageTimeout(
                hwnd, IconNativeMethods.WM_GETICON, new IntPtr(IconNativeMethods.ICON_BIG), IntPtr.Zero,
                IconNativeMethods.SMTO_ABORTIFHUNG | IconNativeMethods.SMTO_BLOCK,
                TimeoutMs, out _);

            if (icon == IntPtr.Zero)
                icon = IconNativeMethods.GetClassLongPtr(hwnd, IconNativeMethods.GCLP_HICON);

            return icon == IntPtr.Zero ? null : Copy(icon);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? Copy(IntPtr icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
