namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

/// <summary>
/// Where the lag-mark button sits: the bottom-right of the primary monitor's work area, inset
/// <see cref="InsetDip"/>, moving UP out of any chrome it would cover — the same keep-clear
/// approach as <see cref="RestartNoticePlacement"/>, for the same reason: a button over the keys,
/// the toolbar or an open menu would be dwelled by a gaze meant for them, and would hide what it
/// covers. The bottom-right is the owner's chosen corner (spec §2.2), so unlike the card, which is
/// centred, this placement only ever moves the button vertically: up, not sideways.
/// </summary>
internal static class LagMarkPlacement
{
    /// <summary>The gap between the button and the work area's right and bottom edges, in DIP.</summary>
    internal const double InsetDip = 24;

    /// <summary>The clear gap kept between the button and any chrome, in DIP.</summary>
    internal const double KeepClearGapDip = 24;

    /// <summary>
    /// The button's top-left. Without chrome in the way, bottom-right, inset <see cref="InsetDip"/>
    /// from the work area's edges. Otherwise the nearest spot to that which clears every zone by
    /// <see cref="KeepClearGapDip"/>: just above the zone it would cover; at worst the top of the
    /// work area.
    /// </summary>
    /// <param name="workArea">The work area in DIP.</param>
    /// <param name="button">The button's full extent in DIP (the visual plus its grown hit area).</param>
    /// <param name="keepClear">The chrome to keep clear of, in DIP (the edge bar, a docked toolbar, the shown keyboard, an open menu).</param>
    /// <returns>The button's left and top in DIP.</returns>
    internal static (double Left, double Top) Place(Rect workArea, Size button, IReadOnlyList<Rect> keepClear)
    {
        var left = workArea.Right - InsetDip - button.Width;
        var bottomTop = workArea.Bottom - InsetDip - button.Height;

        var inflated = keepClear
            .Select(zone => { zone.Inflate(KeepClearGapDip, KeepClearGapDip); return zone; })
            .ToList();

        bool Clear(double top)
        {
            var rect = new Rect(left, top, button.Width, button.Height);
            // A strict overlap: Rect.IntersectsWith counts touching edges, and "just above, a gap
            // clear" puts the button's edge exactly on the inflated zone's.
            return top >= workArea.Top
                && rect.Bottom <= workArea.Bottom
                && !inflated.Any(zone => rect.Right > zone.Left && rect.Left < zone.Right
                                         && rect.Bottom > zone.Top && rect.Top < zone.Bottom);
        }

        if (Clear(bottomTop)) return (left, bottomTop);

        // Just above each zone, nearest to the resting place first (below is off the work area's
        // bottom edge by construction: the button already rests against it).
        var candidates = keepClear
            .Select(zone => zone.Top - KeepClearGapDip - button.Height)
            .OrderBy(top => Math.Abs(top - bottomTop));
        foreach (var top in candidates)
        {
            if (Clear(top)) return (left, top);
        }

        return (left, workArea.Top);
    }
}
