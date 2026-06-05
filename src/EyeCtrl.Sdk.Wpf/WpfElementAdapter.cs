namespace EyeCtrl.Sdk.Wpf;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

internal sealed class WpfElementAdapter : IDwellElementAdapter
{
    private readonly FrameworkElement _element;
    private long _hwndCache;

    public string Id { get; }
    public DwellMode Mode { get; }

    public long WindowHandle
    {
        get
        {
            if (_hwndCache != 0) return _hwndCache;
            if (Window.GetWindow(_element) is { } w)
                _hwndCache = new WindowInteropHelper(w).Handle.ToInt64();
            return _hwndCache;
        }
    }

    public WpfElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        Mode = mode;
    }

    public bool TryGetScreenRect(out SdkRect rect)
    {
        rect = default;
        if (!_element.IsLoaded || _element.ActualWidth <= 0 || _element.ActualHeight <= 0) return false;
        try
        {
            var tl = _element.PointToScreen(new Point(0, 0));
            var br = _element.PointToScreen(new Point(_element.ActualWidth, _element.ActualHeight));
            rect = new SdkRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            return true;
        }
        catch { return false; }
    }

    public void Activate() => _element.Dispatcher.BeginInvoke(() =>
    {
        if (Mode == DwellMode.Focus) { _element.Focus(); return; }

        var peer = UIElementAutomationPeer.CreatePeerForElement(_element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }

        if (_element is System.Windows.Controls.Primitives.ButtonBase b
            && b.Command?.CanExecute(b.CommandParameter) == true)
        { b.Command.Execute(b.CommandParameter); return; }

        System.Diagnostics.Debug.WriteLine($"[EyeCtrl] Dwell activation on '{_element.GetType().Name}' ({Id}) has no invoke target. Element is not focusable, has no UIA invoke pattern, and is not a ButtonBase with a command.");
    });
}
