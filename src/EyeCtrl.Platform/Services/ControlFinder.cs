namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

public class ControlFinder : IControlFinder
{
    private readonly ILogger<ControlFinder> _logger;

    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.Edit, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.TreeItem, ControlType.TabItem,
        ControlType.MenuItem, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.Slider
    };

    private static readonly HashSet<ControlType> NonInteractiveTypes = new()
    {
        ControlType.Pane, ControlType.Document, ControlType.Text,
        ControlType.Group, ControlType.Window, ControlType.Table,
        ControlType.Header, ControlType.TitleBar, ControlType.ToolBar
    };

    public ControlFinder(ILogger<ControlFinder> logger)
    {
        _logger = logger;
    }

    public ControlInfo? FindTarget(PhysicalPoint point)
    {
        try
        {
            var screenPoint = new System.Windows.Point(point.X, point.Y);

            var element = AutomationElement.FromPoint(screenPoint);
            if (element == null)
                return FallbackToWindow(screenPoint);

            var walker = TreeWalker.ControlViewWalker;
            var current = element;
            while (current != null)
            {
                var controlType = current.Current.ControlType;
                if (InteractiveTypes.Contains(controlType))
                {
                    var rect = current.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                    {
                        current = walker.GetParent(current);
                        continue;
                    }
                    return new ControlInfo(new PhysicalRect(rect.X, rect.Y, rect.Width, rect.Height));
                }

                if (NonInteractiveTypes.Contains(controlType))
                {
                    var parent = walker.GetParent(current);
                    if (parent == current) break; // reached root
                    current = parent;
                    continue;
                }

                var nextParent = walker.GetParent(current);
                if (nextParent == current) break; // reached root
                current = nextParent;
            }

            return FallbackToWindow(screenPoint);
        }
        catch (ElementNotAvailableException ex)
        {
            _logger.LogDebug(ex, "UIA element became unavailable while finding target at ({X}, {Y})", point.X, point.Y);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Invalid operation while finding target at ({X}, {Y})", point.X, point.Y);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error while finding target at ({X}, {Y})", point.X, point.Y);
            return null;
        }
    }

    private static ControlInfo? FallbackToWindow(System.Windows.Point point)
    {
        var pt = new POINT { X = (int)point.X, Y = (int)point.Y };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;

        if (!GetClientRect(hwnd, out var clientRect)) return null;

        var topLeft = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref topLeft);

        return new ControlInfo(new PhysicalRect(
            topLeft.X, topLeft.Y, clientRect.Width, clientRect.Height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
