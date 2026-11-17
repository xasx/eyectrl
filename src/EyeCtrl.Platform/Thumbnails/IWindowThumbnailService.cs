namespace EyeCtrl.Platform.Thumbnails;

using EyeCtrl.Core.Models;

/// <summary>
/// Live window previews for the Task Switcher (S4-C): one DWM thumbnail per tile, drawn by DWM
/// INTO the destination window. Lives in <c>EyeCtrl.Platform</c> beside the other window interop
/// because every member is a <c>dwmapi.dll</c> call; the switcher window itself owns only the tile
/// rectangles and the lifecycle.
///
/// <para>
/// <b>Contract.</b> No member throws: a failed registration returns <see cref="IntPtr.Zero"/>, a
/// failed query or update returns <c>false</c>, and a failed unregister is silent. The switcher
/// treats every failure as "this tile keeps its icon box" — a preview is decoration, and must never
/// take the switcher (the owner's only window-switching surface) down.
/// </para>
///
/// <para>
/// Thumbnails draw ABOVE all WPF content of the destination window. A caller must therefore leave
/// the destination rectangle empty of anything that has to be read: the preview box is a hole in
/// the tile, and nothing else is placed inside it while a thumbnail is registered.
/// </para>
/// </summary>
public interface IWindowThumbnailService
{
    /// <summary>
    /// Registers one live thumbnail of <paramref name="sourceHwnd"/> inside
    /// <paramref name="destinationHwnd"/>. The returned handle must be passed to
    /// <see cref="Update"/> and <see cref="Unregister"/>. <see cref="IntPtr.Zero"/> means the
    /// registration failed (non-zero HRESULT, or either handle was zero).
    /// </summary>
    /// <param name="destinationHwnd">The window that will draw the thumbnail (the switcher).</param>
    /// <param name="sourceHwnd">The window whose live picture is shown (a tile's window).</param>
    IntPtr Register(IntPtr destinationHwnd, IntPtr sourceHwnd);

    /// <summary>
    /// The source window's size in physical pixels, as DWM sees it. <c>false</c> when the query
    /// fails or the size is not positive — the caller must then unregister and fall back to the
    /// icon box.
    /// </summary>
    bool TryGetSourceSize(IntPtr thumbnail, out int width, out int height);

    /// <summary>
    /// Places and shows the thumbnail at <paramref name="destination"/> — the destination
    /// window's CLIENT coordinates, in PHYSICAL pixels. Returns <c>false</c> on a non-zero HRESULT.
    /// </summary>
    bool Update(IntPtr thumbnail, PhysicalRect destination);

    /// <summary>Releases the thumbnail. Safe to call on <see cref="IntPtr.Zero"/>.</summary>
    void Unregister(IntPtr thumbnail);
}
