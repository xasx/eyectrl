namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The one canonical implementation of the overlays' screen-edge placement and
/// fullscreen-coverage math. Every overlay previously hand-rolled a WorkArea switch here;
/// the DPI/positioning rules have field-bug history, so they now live in exactly one place.
/// </summary>
internal static class OverlayPositioning
{
    /// <summary>
    /// Where a <paramref name="width"/>×<paramref name="height"/> window sits against the
    /// given work area for an edge name. Edge matching is case-insensitive ("top"/"Top");
    /// unknown values — <see langword="null"/> included — fall back to top. The work area's
    /// <c>Left</c>/<c>Top</c> origin is honored (multi-monitor), which only
    /// QuickActionsGridWindow did before unification.
    /// </summary>
    public static (double Left, double Top) PlaceAtEdge(Rect workArea, string? edge, double width, double height)
    {
        switch (edge?.ToLowerInvariant())
        {
            case "bottom":
                return (workArea.Left + (workArea.Width - width) / 2, workArea.Bottom - height);
            case "left":
                return (workArea.Left, workArea.Top + (workArea.Height - height) / 2);
            case "right":
                return (workArea.Right - width, workArea.Top + (workArea.Height - height) / 2);
            default:
                return (workArea.Left + (workArea.Width - width) / 2, workArea.Top);
        }
    }

    /// <summary>
    /// Sizes <paramref name="window"/> to cover the whole primary monitor (bounds, not
    /// WorkArea — the backdrop must cover the taskbar too). Always leaves the window with a
    /// real fullscreen geometry: when WinForms cannot enumerate a primary screen it falls back
    /// to WPF's own primary-screen metrics rather than leaving Width/Height at their NaN
    /// default. ScrollOverlayWindow copies these straight into its canvas and lays its stop
    /// affordance out on it, so a NaN size would render an unsized window with the only exit
    /// from scroll mode off-canvas — no way out for a gaze-only user.
    /// </summary>
    public static void CoverPrimaryScreen(Window window)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
        {
            window.Left = 0;
            window.Top = 0;
            window.Width = SystemParameters.PrimaryScreenWidth;
            window.Height = SystemParameters.PrimaryScreenHeight;
            return;
        }

        window.Left = screen.Bounds.X / DpiHelper.ScaleX;
        window.Top = screen.Bounds.Y / DpiHelper.ScaleY;
        window.Width = screen.Bounds.Width / DpiHelper.ScaleX;
        window.Height = screen.Bounds.Height / DpiHelper.ScaleY;
    }

    /// <summary>
    /// The offset that puts a docked strip fully clear of its own window, so animating the strip
    /// toward it reads as retracting into the docked edge. Case-insensitive, matching
    /// <see cref="PlaceAtEdge"/> — <c>ToolbarSettings.DockedEdge</c> stores PascalCase.
    ///
    /// <para>
    /// Deliberately does NOT share <see cref="PlaceAtEdge"/>'s fall-back-to-top behaviour.
    /// An unrecognised or <see langword="null"/> edge returns a ZERO vector, which callers read as
    /// "no slide" and degrade to an instant show/hide. Null is the undocked case, and a floating
    /// toolbar has no edge to retract into — falling back to "top" there would animate it upward
    /// for no reason. Do not "fix" this to match its neighbour.
    /// </para>
    ///
    /// <para>
    /// Returns DIPs, not physical pixels: the consumer is a <c>TranslateTransform</c>, and the
    /// extents passed in are <c>ActualWidth</c>/<c>ActualHeight</c>. No <c>DpiHelper</c>
    /// conversion belongs anywhere near this — unlike the auto-hide hit-test rect, which is
    /// physical.
    /// </para>
    /// </summary>
    public static Vector SlideVectorFor(string? edge, double width, double height) =>
        edge?.ToLowerInvariant() switch
        {
            "top" => new Vector(0, -height),
            "bottom" => new Vector(0, height),
            "left" => new Vector(-width, 0),
            "right" => new Vector(width, 0),
            _ => default,
        };

    /// <summary>
    /// A child element's centre in physical screen pixels — the input every dwell-detector
    /// push loop needs. False (with <c>center</c> defaulted) when the element is not yet
    /// rendered or not in the visual tree, the same condition the per-window try/catch
    /// swallowed before. The <see cref="PhysicalPoint"/> return type carries the invariant the
    /// callers depend on: PointToScreen already returns physical pixels, so no DpiHelper
    /// conversion may be applied to the result. (The <c>Point</c> going IN is logical DIPs —
    /// the two live one line apart, which is exactly why the way out is typed.)
    /// </summary>
    public static bool TryGetScreenCenter(System.Windows.FrameworkElement element, out PhysicalPoint center)
    {
        try
        {
            var screenPoint = element.PointToScreen(
                new System.Windows.Point(element.ActualWidth / 2, element.ActualHeight / 2));
            center = new PhysicalPoint(screenPoint.X, screenPoint.Y);
            return true;
        }
        catch (InvalidOperationException)
        {
            center = default;
            return false;
        }
    }
}
