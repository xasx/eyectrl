namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

public class ControlFinder : IControlFinder
{
    private const int SearchStepPixels = 16;
    private const int SearchRadiusPixels = 48;

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

            var exactMatch = ResolveInteractiveElement(AutomationElement.FromPoint(screenPoint));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            var nearbyMatch = FindNearestInteractiveControl(screenPoint);
            if (nearbyMatch != null)
                return nearbyMatch;

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

    private static ControlInfo? ResolveInteractiveElement(AutomationElement? element)
    {
        if (element == null)
            return null;

        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        while (current != null)
        {
            var controlType = current.Current.ControlType;
            if (InteractiveTypes.Contains(controlType))
            {
                var rect = current.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                    return null;

                return new ControlInfo(new PhysicalRect(rect.X, rect.Y, rect.Width, rect.Height));
            }

            var nextParent = walker.GetParent(current);
            if (nextParent == current)
                break;

            if (!NonInteractiveTypes.Contains(controlType) && nextParent == null)
                break;

            current = nextParent;
        }

        return null;
    }

    private static ControlInfo? FindNearestInteractiveControl(System.Windows.Point point)
    {
        ControlInfo? nearest = null;
        var bestDistance = double.MaxValue;
        var seenRects = new HashSet<PhysicalRect>();

        foreach (var samplePoint in EnumerateSamplePoints(point))
        {
            var candidate = ResolveInteractiveElement(AutomationElement.FromPoint(samplePoint));
            if (candidate == null || !seenRects.Add(candidate.BoundingRect))
                continue;

            var distance = DistanceSquaredToRect(point, candidate.BoundingRect);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static IEnumerable<System.Windows.Point> EnumerateSamplePoints(System.Windows.Point origin)
    {
        yield return origin;

        for (var dy = -SearchRadiusPixels; dy <= SearchRadiusPixels; dy += SearchStepPixels)
        {
            for (var dx = -SearchRadiusPixels; dx <= SearchRadiusPixels; dx += SearchStepPixels)
            {
                if (dx == 0 && dy == 0)
                    continue;

                yield return new System.Windows.Point(origin.X + dx, origin.Y + dy);
            }
        }
    }

    private static double DistanceSquaredToRect(System.Windows.Point point, PhysicalRect rect)
    {
        var clampedX = Math.Clamp(point.X, rect.X, rect.X + rect.Width);
        var clampedY = Math.Clamp(point.Y, rect.Y, rect.Y + rect.Height);
        var dx = point.X - clampedX;
        var dy = point.Y - clampedY;
        return (dx * dx) + (dy * dy);
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
