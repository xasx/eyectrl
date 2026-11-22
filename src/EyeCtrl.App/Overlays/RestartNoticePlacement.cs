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
    /// The smaller separation tried only when <see cref="KeepClearGapDip"/> cannot fit anywhere.
    /// Two DIP keeps the card's edge from TOUCHING the chrome: <see cref="Rect.IntersectsWith"/>
    /// counts shared edges, and a gaze at a shared edge is ambiguous.
    /// </summary>
    internal const double MinimumClearGapDip = 2;

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
    /// any of them, inside the work area; at worst the top of the area.
    /// <para>
    /// F17: the 1.6x-tall prediction bar makes the 2.0x keyboard 834 DIP tall. Top-docked on the
    /// 1152 DIP work area (1920x1200 at 200% less the taskbar) only 318 DIP remain below it, 9
    /// too few for the card plus the 24 DIP gap. Before covering the keyboard, the card is placed
    /// with the smallest separation that still fits (<see cref="MinimumClearGapDip"/>): still no
    /// overlap, which is the invariant this placement exists for.
    /// </para>
    /// </summary>
    /// <param name="area">The area to place in, in DIP (the work area, or the full screen).</param>
    /// <param name="card">The card's size in DIP.</param>
    /// <param name="keepClear">The chrome to keep clear of, in DIP (the shown keyboard, a docked toolbar, an open menu).</param>
    /// <returns>The card's left and top in DIP; the top of <paramref name="area"/> when nothing is clear.</returns>
    internal static (double Left, double Top) Place(Rect area, Size card, IReadOnlyList<Rect> keepClear)
    {
        TryPlace(area, card, keepClear, out var position);
        return position;
    }

    /// <summary>
    /// The clear-placement half of <see cref="Place"/>: true when a spot clear of every zone (by
    /// the full gap, else the minimal one) was found. On false, <paramref name="position"/> is the
    /// old fallback at the top of <paramref name="area"/>; the caller may still try another area.
    /// </summary>
    internal static bool TryPlace(Rect area, Size card, IReadOnlyList<Rect> keepClear,
        out (double Left, double Top) position)
    {
        var left = area.Left + (area.Width - card.Width) / 2;
        var bottomTop = area.Bottom - card.Height - MarginDip;

        bool Clear(double top, double gap)
        {
            var rect = new Rect(left, top, card.Width, card.Height);
            // A strict overlap: Rect.IntersectsWith counts touching edges, and "just above, a gap
            // clear" puts the card's edge exactly on the zone plus the gap.
            return top >= area.Top
                && rect.Bottom <= area.Bottom
                && !keepClear.Any(zone =>
                    rect.Right > zone.Left - gap && rect.Left < zone.Right + gap
                    && rect.Bottom > zone.Top - gap && rect.Top < zone.Bottom + gap);
        }

        IEnumerable<double> CandidateTops(double gap) =>
            keepClear
                .SelectMany(zone => new[] { zone.Top - gap - card.Height, zone.Bottom + gap })
                .OrderBy(top => System.Math.Abs(top - bottomTop));

        if (Clear(bottomTop, KeepClearGapDip))
        {
            position = (left, bottomTop);
            return true;
        }

        // Just above or just below each zone, nearest to the resting place first, with the full
        // clear gap. Only when no gapped spot fits does the minimal-separation pass run.
        foreach (var top in CandidateTops(KeepClearGapDip))
        {
            if (!Clear(top, KeepClearGapDip)) continue;
            position = (left, top);
            return true;
        }

        foreach (var top in CandidateTops(MinimumClearGapDip))
        {
            if (!Clear(top, MinimumClearGapDip)) continue;
            position = (left, top);
            return true;
        }

        position = (left, area.Top);
        return false;
    }
}
