namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using Microsoft.Extensions.Logging;

public class ControlFinder : IControlFinder
{
    private const int SearchStepPixels = 16;

    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly ILogger<ControlFinder> _logger;
    private readonly AutoSelectionSettings _settings;

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

    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="settings">
    /// Live AutoSelection settings. Held by reference (not snapshotted) so that edits to
    /// <see cref="AutoSelectionSettings.SnapSearchRadiusPixels"/> take effect on the next
    /// lookup — this type is a DI singleton while the settings are user-editable at runtime.
    /// </param>
    public ControlFinder(ILogger<ControlFinder> logger, AutoSelectionSettings settings)
    {
        _logger = logger;
        _settings = settings;
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

            // Read the radius per call, not per construction: this is a DI singleton and the
            // setting is live-editable from the Selection settings page.
            var nearbyMatch = FindNearestInteractiveControl(screenPoint, _settings.SnapSearchRadiusPixels);
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
    /// <param name="origin">Gaze point to search around.</param>
    /// <param name="maxRadius">
    /// Outermost ring radius in physical pixels, from
    /// <see cref="AutoSelectionSettings.SnapSearchRadiusPixels"/>. Each additional ring is up to
    /// 8 further UIA probes against that same ~500ms budget, which is why the setting is capped.
    /// </param>
    private static ControlInfo? FindNearestInteractiveControl(System.Windows.Point origin, int maxRadius)
    {
        var seenRects = new HashSet<PhysicalRect>();

        foreach (var radius in EnumerateSearchRadii(maxRadius))
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

    /// <summary>
    /// Yields the ring radii the sweep will probe, innermost first: one ring per whole
    /// <see cref="SearchStepPixels"/> that fits inside <paramref name="maxRadius"/>. Radii below
    /// one full step yield nothing, which degrades the search to "exact hit under the gaze point
    /// only" rather than throwing. Exposed for headless testing — the sweep itself needs UIA.
    /// </summary>
    internal static IEnumerable<int> EnumerateSearchRadii(int maxRadius)
    {
        for (var radius = SearchStepPixels; radius <= maxRadius; radius += SearchStepPixels)
            yield return radius;
    }

    internal static IEnumerable<System.Windows.Point> EnumerateRingPoints(System.Windows.Point origin, int radius)
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

    internal static double DistanceSquaredToRect(System.Windows.Point point, PhysicalRect rect)
    {
        var nearest = new PhysicalPoint(
            Math.Clamp(point.X, rect.X, rect.X + rect.Width),
            Math.Clamp(point.Y, rect.Y, rect.Y + rect.Height));
        return new PhysicalPoint(point.X, point.Y).DistanceSquaredTo(nearest);
    }

    private static ControlInfo? FallbackToWindow(System.Windows.Point point)
    {
        var pt = new NativeMethods.POINT { X = (int)point.X, Y = (int)point.Y };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;

        if (!GetClientRect(hwnd, out var clientRect)) return null;

        var topLeft = new NativeMethods.POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref topLeft);

        return new ControlInfo(new PhysicalRect(
            topLeft.X, topLeft.Y, clientRect.Width, clientRect.Height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT Point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativeMethods.POINT lpPoint);
}
