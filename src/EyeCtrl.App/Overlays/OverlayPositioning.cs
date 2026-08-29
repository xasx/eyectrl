namespace EyeCtrl.App.Overlays;

using System.Windows;
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
    /// unknown values fall back to top. The work area's <c>Left</c>/<c>Top</c> origin is
    /// honored (multi-monitor), which only QuickActionsGridWindow did before unification.
    /// </summary>
    public static (double Left, double Top) PlaceAtEdge(Rect workArea, string edge, double width, double height)
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
    /// WorkArea — the backdrop must cover the taskbar too). Null-guarded: a missing primary
    /// screen leaves the window's geometry untouched rather than throwing.
    /// </summary>
    public static void CoverPrimaryScreen(Window window)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
            return;

        window.Left = screen.Bounds.X / DpiHelper.ScaleX;
        window.Top = screen.Bounds.Y / DpiHelper.ScaleY;
        window.Width = screen.Bounds.Width / DpiHelper.ScaleX;
        window.Height = screen.Bounds.Height / DpiHelper.ScaleY;
    }

    /// <summary>
    /// A child element's centre in physical screen pixels — the input every dwell-detector
    /// push loop needs. False (with <c>center</c> defaulted) when the element is not yet
    /// rendered or not in the visual tree, the same condition the per-window try/catch
    /// swallowed before. The result is PHYSICAL: PointToScreen already returns physical
    /// pixels, so no DpiHelper conversion may be applied to it.
    /// </summary>
    public static bool TryGetScreenCenter(System.Windows.FrameworkElement element, out System.Windows.Point center)
    {
        try
        {
            center = element.PointToScreen(
                new System.Windows.Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return true;
        }
        catch (InvalidOperationException)
        {
            center = default;
            return false;
        }
    }
}
