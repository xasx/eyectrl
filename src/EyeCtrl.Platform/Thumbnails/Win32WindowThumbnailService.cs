namespace EyeCtrl.Platform.Thumbnails;

using EyeCtrl.Core.Models;

/// <summary>
/// The real <see cref="IWindowThumbnailService"/>: three <c>dwmapi.dll</c> thumbnail calls per tile,
/// no window messages, no waits. DWM composes the source window's surface itself, so a hung source
/// application cannot stall the caller — the calls register and describe the thumbnail; they never
/// ask the source to paint.
///
/// <para>
/// The declarations live in <see cref="DwmThumbnailNative"/> and are checked against
/// <c>dwmapi.h</c> (the repo lesson: a struct or flag typo once looked like "Windows refuses").
/// Every failure is turned into the seam's failure value — never an exception — because the
/// switcher calls this from its post-layout registration pass and a preview is decoration only.
/// The failing HRESULT is reported through the matching <c>out</c> parameter; it carries no title,
/// process or window content, so the open line can log it.
/// </para>
/// </summary>
public sealed class Win32WindowThumbnailService : IWindowThumbnailService
{
    /// <summary><c>E_INVALIDARG</c> (winerror.h): a zero handle never reaches DWM.</summary>
    internal const int EInvalidArg = unchecked((int)0x80070057);

    /// <inheritdoc />
    public IntPtr Register(IntPtr destinationHwnd, IntPtr sourceHwnd, out int hresult)
    {
        if (destinationHwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero)
        {
            hresult = EInvalidArg;
            return IntPtr.Zero;
        }

        hresult = DwmThumbnailNative.DwmRegisterThumbnail(
            destinationHwnd, sourceHwnd, out var thumbnail);
        if (hresult != 0 || thumbnail == IntPtr.Zero)
            return IntPtr.Zero;

        return thumbnail;
    }

    /// <inheritdoc />
    public bool TryGetSourceSize(IntPtr thumbnail, out int width, out int height, out int hresult)
    {
        width = 0;
        height = 0;
        if (thumbnail == IntPtr.Zero)
        {
            hresult = EInvalidArg;
            return false;
        }

        hresult = DwmThumbnailNative.DwmQueryThumbnailSourceSize(thumbnail, out var size);
        if (hresult != 0 || size.cx <= 0 || size.cy <= 0)
            return false;

        width = size.cx;
        height = size.cy;
        return true;
    }

    /// <inheritdoc />
    public bool Update(IntPtr thumbnail, PhysicalRect destination, out int hresult)
    {
        if (thumbnail == IntPtr.Zero)
        {
            hresult = EInvalidArg;
            return false;
        }

        var properties = new DwmThumbnailNative.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmThumbnailNative.SwitcherPropertyFlags,
            rcDestination = new DwmThumbnailNative.RECT
            {
                // The geometry helper already rounded every edge to whole physical pixels; the cast
                // is exact.
                Left = (int)destination.X,
                Top = (int)destination.Y,
                Right = (int)(destination.X + destination.Width),
                Bottom = (int)(destination.Y + destination.Height),
            },
            opacity = DwmThumbnailNative.Opaque,
            fVisible = true,
            // The whole window, title bar included (the 17.11 trial's setting). The flag is named
            // here because the property is specified: leaving it out would let the default decide.
            fSourceClientAreaOnly = false,
        };

        hresult = DwmThumbnailNative.DwmUpdateThumbnailProperties(thumbnail, ref properties);
        return hresult == 0;
    }

    /// <inheritdoc />
    public void Unregister(IntPtr thumbnail)
    {
        if (thumbnail == IntPtr.Zero)
            return;

        _ = DwmThumbnailNative.DwmUnregisterThumbnail(thumbnail);
    }
}
