namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>One window's icon request: the seam is per WINDOW, not per process (S3-B).</summary>
/// <param name="Hwnd">The window handle.</param>
/// <param name="ProcessId">The window's owning process.</param>
internal readonly record struct IconRequest(IntPtr Hwnd, uint ProcessId);

/// <summary>
/// The icon sources in decision order. The VALUES are the order — <see cref="AppIconResolver"/>
/// walks them canonically, so a differently ordered source list cannot change the outcome.
/// </summary>
internal enum AppIconSourceKind
{
    /// <summary>A packaged (Store/UWP) app's own icon, by the frame window's AUMID.</summary>
    PackagedApp,

    /// <summary>The window's executable icon, read through the shell at 64–96 px.</summary>
    Executable,

    /// <summary>The window's own icon (<c>WM_GETICON</c> / the class icon), small by nature.</summary>
    Window,

    /// <summary>The neutral drawn glyph that guarantees an icon is always shown.</summary>
    Default,
}

/// <summary>
/// One addressable icon source. Each source is asked only after every earlier source in
/// <see cref="AppIconResolver.Order"/> declined or failed, and a source that throws is contained by
/// the resolver — an icon is decoration; it may never break the switcher.
/// </summary>
internal interface IAppIconSource
{
    /// <summary>Which position in the canonical order this source occupies.</summary>
    AppIconSourceKind Kind { get; }

    /// <summary>
    /// Tries to produce the request's icon. Returns null when this source does not apply or found
    /// nothing (the next source is then asked).
    /// </summary>
    ImageSource? TryGetIcon(IconRequest request);
}
