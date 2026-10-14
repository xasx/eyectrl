namespace EyeCtrl.App.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Where the restart notice card sits (holistic review F5): centred, 48 DIP above the bottom of
/// the work area, like the caregiver alert, except that it must NEVER cover EyeCtrl's own gaze
/// chrome. The caregiver alert shares the bottom-centre spot and does not look at the chrome (it is
/// click-through with no dwell target of its own); this card has a dwell button, and a button over
/// the keys or the toolbar would be dwelled by a gaze meant for them, and hide what it covers.
/// "Chrome" is exactly what the activation offer already keeps clear of
/// (<c>ActivationChromeZones</c>: the keyboard, a docked toolbar, an open menu).
/// </summary>
internal static class RestartNoticePlacement
{
    /// <summary>The gap between the card's bottom edge and the work area's bottom, in DIP.</summary>
    internal const double MarginDip = 48;

    /// <summary>The clear gap kept between the card and any chrome, in DIP.</summary>
    internal const double KeepClearGapDip = 24;

    /// <summary>
    /// The chrome zones the activation offer keeps clear of, converted to DIP (they are published
    /// in physical pixels).
    /// </summary>
    /// <param name="zones">The zones in physical pixels.</param>
    /// <returns>The same rectangles in DIP, empty rectangles dropped.</returns>
    internal static IReadOnlyList<Rect> ToDip(IEnumerable<PhysicalRect> zones) =>
        zones
            .Where(zone => zone.Width > 0 && zone.Height > 0)
            .Select(zone => new Rect(
                DpiHelper.PhysicalToLogicalX(zone.X), DpiHelper.PhysicalToLogicalY(zone.Y),
                DpiHelper.PhysicalToLogicalX(zone.Width), DpiHelper.PhysicalToLogicalY(zone.Height)))
            .ToList();

    /// <summary>
    /// The card's top-left. Without chrome in the way, bottom-centre. Otherwise the nearest spot
    /// to that which clears every zone by <see cref="KeepClearGapDip"/>: just above or just below
    /// any of them, inside the work area; at worst the top of the work area.
    /// </summary>
    /// <param name="workArea">The work area in DIP.</param>
    /// <param name="card">The card's size in DIP.</param>
    /// <param name="keepClear">The chrome to keep clear of, in DIP (the shown keyboard, a docked toolbar, an open menu).</param>
    /// <returns>The card's left and top in DIP.</returns>
    internal static (double Left, double Top) Place(Rect workArea, Size card, IReadOnlyList<Rect> keepClear)
    {
        var left = workArea.Left + (workArea.Width - card.Width) / 2;
        var bottomTop = workArea.Bottom - card.Height - MarginDip;

        var inflated = keepClear
            .Select(zone => { zone.Inflate(KeepClearGapDip, KeepClearGapDip); return zone; })
            .ToList();

        bool Clear(double top)
        {
            var rect = new Rect(left, top, card.Width, card.Height);
            // A strict overlap: Rect.IntersectsWith counts touching edges, and "just above, a gap
            // clear" puts the card's edge exactly on the inflated zone's.
            return top >= workArea.Top
                && rect.Bottom <= workArea.Bottom
                && !inflated.Any(zone => rect.Right > zone.Left && rect.Left < zone.Right
                                         && rect.Bottom > zone.Top && rect.Top < zone.Bottom);
        }

        if (Clear(bottomTop)) return (left, bottomTop);

        // Just above or just below each zone, nearest to the resting place first.
        var candidates = keepClear
            .SelectMany(zone => new[] { zone.Top - KeepClearGapDip - card.Height, zone.Bottom + KeepClearGapDip })
            .OrderBy(top => System.Math.Abs(top - bottomTop));
        foreach (var top in candidates)
        {
            if (Clear(top)) return (left, top);
        }

        return (left, workArea.Top);
    }
}
