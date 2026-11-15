namespace EyeCtrl.Platform.Icons;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// <see cref="IShellImageSource"/> over <c>SHCreateItemFromParsingName</c> +
/// <c>IShellItemImageFactory::GetImage</c>: the same call Explorer uses, which is why it returns the
/// app's real multi-size icon (at least 64 px, usually 96 or larger) for both an exe path and a
/// packaged app's AUMID.
///
/// <para>
/// Every COM object and GDI handle is released in a finally block. Failures (a vanished app, an
/// unreadable item) return null — never an exception, never a half-initialised bitmap.
/// </para>
/// </summary>
internal sealed class ShellImageSource : IShellImageSource
{
    /// <summary>
    /// The requested edge in physical pixels. 40 DIP at the owner's 200 % is 80 px, so 96 px leaves a
    /// small downscale (sharper than an exact-size request) and still comes from a real icon size
    /// (96 is a shell icon size; 80 is not).
    /// </summary>
    internal const int RequestedSizePx = 96;

    public ImageSource? TryGetImage(string parsingName, int sizePx)
    {
        if (string.IsNullOrWhiteSpace(parsingName) || sizePx <= 0)
            return null;

        object? itemObject = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IconNativeMethods.IShellItemImageFactory).GUID;
            if (IconNativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out itemObject) != 0)
                return null;
            if (itemObject is not IconNativeMethods.IShellItemImageFactory factory)
                return null;

            // ICONONLY: never a thumbnail (an exe has no useful one, and a packaged app's tile must
            // be its icon, not a screenshot). BIGGERSIZEOK: take a larger source when the icon file
            // has one — WPF's high-quality scaling then draws 40 DIP from it.
            var size = new IconNativeMethods.Size { Width = sizePx, Height = sizePx };
            var flags = IconNativeMethods.SIIGBF_ICONONLY | IconNativeMethods.SIIGBF_BIGGERSIZEOK;
            if (factory.GetImage(size, flags, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Frozen: the same instance is cached per exe/AUMID and shown on the UI thread after
            // being produced wherever the first open happened.
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                _ = IconNativeMethods.DeleteObject(bitmap);
            if (itemObject is not null)
                _ = Marshal.ReleaseComObject(itemObject);
        }
    }
}
