namespace EyeCtrl.Sdk.WinUI;

using Microsoft.UI.Xaml;

/// <summary>
/// Optional fallback for WinUI hosts. The adapter normally resolves each control's owning
/// window per-element via XamlRoot.ContentIslandEnvironment (Windows App SDK 1.4+), which is
/// correct for multi-window apps. This explicit HWND is used only when that per-element path
/// is unavailable. Note: it is a process-wide value, so it suits single-window hosts;
/// multi-window hosts should rely on the implicit per-element resolution and need not call this.
/// </summary>
public static class EyeCtrlDwell
{
    internal static long ExplicitHwnd;

    public static void Initialize(Window window)
        => ExplicitHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64();
}
