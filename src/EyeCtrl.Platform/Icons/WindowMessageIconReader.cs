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
/// <b>Where the icon is.</b> <c>SendMessageTimeout</c>'s RETURN value is only success/failure; the
/// message's result — the HICON — comes back in <c>lpdwResult</c>. Reading the return value as the
/// handle (S3-B's first shape) made this source dead for every RESPONSIVE window: the answer 1 was
/// copied as an icon handle, the copy threw, and the class fallback was skipped too. The message
/// icon and the class icon are therefore tried independently: a zero, timed-out or uncopyable
/// message icon still falls through to the class icon, which is read without waiting on the window.
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

        var messageIcon = SendGetIcon(hwnd);
        if (messageIcon != IntPtr.Zero && Copy(messageIcon) is { } fromMessage)
            return fromMessage;

        // Independent of the message icon and of its copy: a window can answer WM_GETICON with 0
        // (live ApplicationFrameWindows do), time out, or hand back a handle that cannot be copied;
        // the class icon is still worth a try, and reading it sends no message and waits on nothing.
        var classIcon = ReadClassIcon(hwnd);
        if (classIcon == IntPtr.Zero || classIcon == messageIcon)
            return null;

        return Copy(classIcon);
    }

    /// <summary>The window's own big icon from <c>WM_GETICON</c>, or 0 when it has none.</summary>
    private static IntPtr SendGetIcon(IntPtr hwnd)
    {
        try
        {
            // SMTO_BLOCK keeps our own thread from re-entering the dispatcher while it waits (bounded
            // by TimeoutMs); ABORTIFHUNG returns at once when the target is already hung.
            var delivered = IconNativeMethods.SendMessageTimeout(
                hwnd, IconNativeMethods.WM_GETICON, new IntPtr(IconNativeMethods.ICON_BIG), IntPtr.Zero,
                IconNativeMethods.SMTO_ABORTIFHUNG | IconNativeMethods.SMTO_BLOCK,
                TimeoutMs, out var result);

            // Only a delivered message has a meaningful result (the HICON); a timeout's result is
            // unspecified and must not be copied.
            return delivered != IntPtr.Zero ? result : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>The window class's big icon, or 0. Sends no message: a hung window cannot stall it.</summary>
    private static IntPtr ReadClassIcon(IntPtr hwnd)
    {
        try
        {
            return IconNativeMethods.GetClassLongPtr(hwnd, IconNativeMethods.GCLP_HICON);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Copies a borrowed HICON into a frozen WPF bitmap; null when the handle is unusable.</summary>
    private static ImageSource? Copy(IntPtr icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
