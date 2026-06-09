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

    public string Id { get; }
    public DwellMode Mode { get; }

    public WinUiElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        Mode = mode;
    }

    public long WindowHandle => ResolveHwnd();

    private long ResolveHwnd()
    {
        var env = _element.XamlRoot?.ContentIslandEnvironment;
        if (env is not null)
            return Win32Interop.GetWindowFromWindowId(env.AppWindowId).ToInt64();
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

    public void Activate() => _element.DispatcherQueue.TryEnqueue(() =>
    {
        if (Mode == DwellMode.Focus) { _element.Focus(FocusState.Programmatic); return; }

        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(_element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }

        if (_element is ButtonBase b && b.Command?.CanExecute(b.CommandParameter) == true)
            b.Command.Execute(b.CommandParameter);
    });

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
}
