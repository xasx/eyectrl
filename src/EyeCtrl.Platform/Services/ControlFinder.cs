namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

public class ControlFinder : IControlFinder
{
    private const int SearchStepPixels = 16;
    private const int SearchRadiusPixels = 48;

    private static readonly int OwnProcessId = Environment.ProcessId;

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

        // Never target EyeCtrl's own overlay windows (cursor circle, activation button,
        // highlight, etc.), which can sit under the gaze point as topmost windows and
        // would otherwise be returned by AutomationElement.FromPoint.
        if (element.Current.ProcessId == OwnProcessId)
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

    /// <summary>
    /// Probes in expanding square rings around the gaze point and returns as soon as a ring
    /// yields a hit, choosing the nearest candidate within that ring. The early-exit keeps the
    /// common (imprecise-gaze) path to roughly one ring (~8 UIA probes) instead of a full grid
    /// sweep, which matters because each <see cref="AutomationElement.FromPoint"/> is a
    /// cross-process COM call and the caller budgets only ~500ms for the whole resolution.
    /// </summary>
    private static ControlInfo? FindNearestInteractiveControl(System.Windows.Point origin)
    {
        var seenRects = new HashSet<PhysicalRect>();

        for (var radius = SearchStepPixels; radius <= SearchRadiusPixels; radius += SearchStepPixels)
        {
            ControlInfo? best = null;
            var bestDistance = double.MaxValue;

            foreach (var samplePoint in EnumerateRingPoints(origin, radius))
            {
                ControlInfo? candidate;
                try
                {
                    candidate = ResolveInteractiveElement(AutomationElement.FromPoint(samplePoint));
                }
                catch (Exception)
                {
                    // A transient UIA failure at a single sample point must not abort the sweep.
                    continue;
                }

                if (candidate == null || !seenRects.Add(candidate.BoundingRect))
                    continue;

                var distance = DistanceSquaredToRect(origin, candidate.BoundingRect);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best != null)
                return best;
        }

        return null;
    }

    private static IEnumerable<System.Windows.Point> EnumerateRingPoints(System.Windows.Point origin, int radius)
    {
        yield return new System.Windows.Point(origin.X - radius, origin.Y - radius);
        yield return new System.Windows.Point(origin.X,          origin.Y - radius);
        yield return new System.Windows.Point(origin.X + radius, origin.Y - radius);
        yield return new System.Windows.Point(origin.X - radius, origin.Y);
        yield return new System.Windows.Point(origin.X + radius, origin.Y);
        yield return new System.Windows.Point(origin.X - radius, origin.Y + radius);
        yield return new System.Windows.Point(origin.X,          origin.Y + radius);
        yield return new System.Windows.Point(origin.X + radius, origin.Y + radius);
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
