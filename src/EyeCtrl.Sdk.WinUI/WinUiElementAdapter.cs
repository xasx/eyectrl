namespace EyeCtrl.Sdk.WinUI;

using System.Runtime.InteropServices;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

internal sealed class WinUiElementAdapter : IDwellElementAdapter
{
    private readonly FrameworkElement _element;
    private long _hwndCache;

    public string Id { get; }
    public DwellMode Mode => Dwell.GetMode(_element);

    public WinUiElementAdapter(string id, FrameworkElement element)
    {
        Id = id;
        _element = element;
    }

    public long WindowHandle => ResolveHwnd();

    private long ResolveHwnd()
    {
        if (_hwndCache != 0) return _hwndCache;
        var env = _element.XamlRoot?.ContentIslandEnvironment;
        if (env is not null)
        {
            _hwndCache = Win32Interop.GetWindowFromWindowId(env.AppWindowId).ToInt64();
            return _hwndCache;
        }
        return EyeCtrlDwell.ExplicitHwnd;
    }

    public bool TryGetScreenRect(out SdkRect rect)
    {
        rect = default;
        var xr = _element.XamlRoot;
        if (xr is null || _element.ActualWidth <= 0 || _element.ActualHeight <= 0) return false;

        var hwnd = WindowHandle;
        if (hwnd == 0) return false;

        try
        {
            // dip is the element's offset from the XamlRoot content root, in DIPs; origin is the
            // client-area top-left of the root window in physical px. This assumes the XamlRoot
            // content root sits at that window's client origin — true for a standard top-level
            // WinUI 3 Window. A XAML island offset inside a larger Win32 host would need that
            // island's own screen origin instead.
            var dip = _element.TransformToVisual(xr.Content).TransformPoint(new Point(0, 0));
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(new IntPtr(hwnd), ref origin)) return false;

            rect = WinUiCoords.ToScreenRect(
                dip.X, dip.Y, _element.ActualWidth, _element.ActualHeight,
                xr.RasterizationScale, origin.X, origin.Y);
            return true;
        }
        catch { return false; }
    }

    public void Activate()
    {
        if (!_element.DispatcherQueue.TryEnqueue(() =>
        {
            if (Mode == DwellMode.Focus) { _element.Focus(FocusState.Programmatic); return; }

            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(_element);
            if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }

            if (_element is ButtonBase b && b.Command?.CanExecute(b.CommandParameter) == true)
            { b.Command.Execute(b.CommandParameter); return; }

            System.Diagnostics.Debug.WriteLine($"[EyeCtrl] Dwell activation on '{_element.GetType().Name}' ({Id}) has no invoke target. Element is not focusable, has no UIA invoke pattern, and is not a ButtonBase with a command.");
        }))
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCtrl] Dwell activation on '{Id}' dropped: dispatcher queue is shut down.");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
}
