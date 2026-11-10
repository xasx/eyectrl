namespace EyeCtrl.Core.Keyboard;

using System.Collections.Generic;
using EyeCtrl.Core.Models;

/// <summary>
/// Which on-screen key a gaze sample targets: <b>box-first</b>, then nearest centre within a small
/// band around that key's box.
///
/// <para>
/// Nearest-centre alone gives each key its Voronoi cell, not its visible box, so a gaze inside a
/// key near its edge can target the neighbour whose centre happens to be closer — a wide key
/// (Caps, Shift, Space) loses part of its own box that way. For a user whose fixation is unsteady,
/// a key that dwells on its neighbour while looked at inside its own edge is both an error source
/// and a trust problem. So:
/// </para>
/// <list type="number">
/// <item>If the gaze is inside one or more key boxes (edges inclusive), the key among THOSE whose
/// centre is nearest wins. Touching or overlapping boxes resolve that way.</item>
/// <item>Otherwise — the gaps between keys and the row indents — the nearest centre wins only
/// while the gaze is within 8 physical px of THAT key's box. Further out there is no target at all
/// (<c>null</c>), so the empty space beside a ragged row's end cannot fire its outermost key. The
/// 8 px band is the 4-DIP key gap at 200 % scale, so the gutters between adjacent keys still
/// resolve to the nearer one.</item>
/// </list>
///
/// <para>
/// Ties (equal squared distance) go to the key enumerated first, i.e. layout order: both passes
/// compare with a strict <c>&lt;</c>, as the original loop did, so the result is deterministic for
/// a given layout. A key with a centre but no box takes part in pass 2 only when no boxes were
/// supplied at all; with boxes present its pick cannot be validated, so it is refused. All
/// coordinates are physical screen pixels — the centres and the boxes must come from the same
/// transform, or a mixed-unit box silently kills keys at any scale above 1.0 (the keyboard-gate
/// DIP defect).
/// </para>
/// </summary>
public static class KeyTargeting
{
    /// <summary>
    /// How far outside a key's box the gaze may rest and still target that key: 8 physical px, the
    /// 4-DIP gutter at 200 % scale. Beyond it a nearest-centre pick is empty space, not a reach for
    /// the key.
    /// </summary>
    private const double MaxPickGapPixels = 8.0;

    /// <summary>
    /// The targeted key id, or null when nothing is within reach. A null (or empty)
    /// <paramref name="boxes"/> skips the box rules and is plain nearest-centre.
    /// </summary>
    public static string? PickKey(
        PhysicalPoint gaze,
        IReadOnlyDictionary<string, PhysicalPoint> centres,
        IReadOnlyDictionary<string, PhysicalRect>? boxes)
    {
        if (boxes is { Count: > 0 })
        {
            string? insideId = null;
            double insideDist = double.MaxValue;
            foreach (var (id, center) in centres)
            {
                if (!boxes.TryGetValue(id, out var box) || !box.Contains(gaze.X, gaze.Y))
                    continue;
                double dist = gaze.DistanceSquaredTo(center);
                if (dist < insideDist)
                {
                    insideDist = dist;
                    insideId = id;
                }
            }
            if (insideId != null)
                return insideId;
        }

        string? nearestId = null;
        double nearestDist = double.MaxValue;
        foreach (var (id, center) in centres)
        {
            double dist = gaze.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId == null || boxes is not { Count: > 0 })
            return nearestId;

        // The nearest centre outside every box is only a reach for THAT key while it is inside the
        // gap band around the key's own box. A key whose box is missing cannot be validated, so it
        // is refused rather than trusted.
        if (!boxes.TryGetValue(nearestId, out var nearestBox))
            return null;
        return DistanceToBox(gaze, nearestBox) <= MaxPickGapPixels ? nearestId : null;
    }

    /// <summary>
    /// Euclidean distance from <paramref name="point"/> to the nearest edge of
    /// <paramref name="box"/>; 0 when the point is inside.
    /// </summary>
    private static double DistanceToBox(PhysicalPoint point, PhysicalRect box)
    {
        double dx = Math.Max(0, Math.Max(box.X - point.X, point.X - (box.X + box.Width)));
        double dy = Math.Max(0, Math.Max(box.Y - point.Y, point.Y - (box.Y + box.Height)));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
