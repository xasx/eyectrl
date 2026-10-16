namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Overlays;

/// <summary>
/// SP-8 fixup 02: host-independent probe geometry. A WPF <c>Window</c>'s top-level HWND is clamped
/// by the OS to the host's maximum track size (about the virtual screen), and WPF writes the
/// clamped size back into <c>Width</c>/<c>Height</c> — so on CI's small runner the probe laid itself
/// out smaller than the tests asked (the corner digit's bottom 764 at a requested 1200, and Start's
/// and End test's hit areas collapsing into each other).
///
/// <para>
/// The mechanism (raise the maximum track size through <c>WM_GETMINMAXINFO</c>, then re-apply the
/// requested size) now lives once, in <see cref="OverlayWindowTestHarness"/> — the T0(a) lag-mark
/// ticket merged second and needed the same thing for its own overlay — and this typed entry point
/// keeps every probe test's call site unchanged.
/// </para>
/// </summary>
internal static class ProbeWindowTestHarness
{
    /// <summary>
    /// Sets <paramref name="window"/>'s requested size and creates its HWND, then forces that size
    /// through the host's track clamp. Never shows the window; the caller drives phases and layout
    /// exactly as before. The hook lives on the window's own <see cref="System.Windows.Interop.HwndSource"/>
    /// until it closes, so every later resize keeps the raised cap too.
    /// </summary>
    public static void EnsureHandleAt(SubPixelProbeWindow window, double widthDip, double heightDip) =>
        OverlayWindowTestHarness.EnsureHandleAt(window, widthDip, heightDip);
}
