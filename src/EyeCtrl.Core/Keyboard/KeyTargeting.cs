namespace EyeCtrl.Core.Keyboard;

using System.Collections.Generic;
using EyeCtrl.Core.Models;

/// <summary>
/// Which on-screen key a gaze sample targets: <b>box-first</b>, then nearest centre among the keys
/// whose box is within a small band of the gaze.
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
/// <item>Otherwise — the gaps between keys and the row indents — the nearest centre wins among the
/// keys whose box is within 8 physical px of the gaze. If no box is that close there is no target
/// at all (<c>null</c>), so the empty space beside a ragged row's end cannot fire its outermost
/// key. The 8 px band is the 4-DIP key gap at 200 % scale, so the gutters between adjacent keys
/// still resolve to the nearer one. The band is measured against EVERY box, not only the box of the
/// nearest centre: at a boundary between rows of different widths the nearest centre can belong to
/// a key whose box is far away while another key's box is only a pixel or two from the gaze.</item>
/// </list>
///
/// <para>
/// Ties (equal squared distance) go to the key enumerated first, i.e. layout order: both passes
/// compare with a strict <c>&lt;</c>, as the original loop did, so the result is deterministic for
/// a given layout. A key with a centre but no box is not a pass-2 candidate when boxes were
/// supplied at all: its reach cannot be validated, so it is refused. All coordinates are physical
/// screen pixels — the centres and the boxes must come from the same transform, or a mixed-unit
/// box silently kills keys at any scale above 1.0 (the keyboard-gate DIP defect).
/// </para>
/// </summary>
public static class KeyTargeting
{
    /// <summary>
    /// How far outside a key's box the gaze may rest and still target that key: 8 physical px, the
    /// 4-DIP gutter at 200 % scale. Beyond it the key is not a pass-2 candidate and, when no other
    /// box is in reach, the pick is empty space rather than a reach for the nearest centre.
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
        var activeBoxes = boxes is { Count: > 0 } ? boxes : null;

        if (activeBoxes != null)
        {
            string? insideId = null;
            double insideDist = double.MaxValue;
            foreach (var (id, center) in centres)
            {
                if (!activeBoxes.TryGetValue(id, out var box) || !box.Contains(gaze.X, gaze.Y))
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

        // Pass 2: only keys whose OWN box is within the band are candidates; among those the
        // nearest centre wins. Measuring the band against every box (not only the nearest centre's
        // box) keeps the between-row gutters alive where the rows have keys of different widths —
        // there the nearest centre can belong to a key whose box is far away while another box is
        // only a pixel or two from the gaze. A key without a box is not a candidate when boxes were
        // supplied: its reach cannot be validated, so it is refused rather than trusted.
        string? nearestId = null;
        double nearestDist = double.MaxValue;
        foreach (var (id, center) in centres)
        {
            if (activeBoxes != null
                && (!activeBoxes.TryGetValue(id, out var box) || DistanceToBox(gaze, box) > MaxPickGapPixels))
            {
                continue;
            }

            double dist = gaze.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }
        return nearestId;
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
