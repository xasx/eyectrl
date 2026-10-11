namespace EyeCtrl.Core.Keyboard;

using System.Collections.Generic;
using EyeCtrl.Core.Models;

/// <summary>
/// Which on-screen key a gaze sample targets: <b>box-first</b>, then nearest centre.
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
/// <item>Otherwise — the gaps between keys and the row indents — the nearest centre over ALL keys
/// wins, exactly the rule the keyboard has always used, so every gap still belongs to a key.</item>
/// </list>
///
/// <para>
/// Ties (equal squared distance) go to the key enumerated first, i.e. layout order: both passes
/// compare with a strict <c>&lt;</c>, as the original loop did, so the result is deterministic for
/// a given layout. A key with a centre but no box takes part in pass 2 only. All coordinates are
/// physical screen pixels — the centres and the boxes must come from the same transform, or a
/// mixed-unit box silently kills keys at any scale above 1.0 (the keyboard-gate DIP defect).
/// </para>
/// </summary>
public static class KeyTargeting
{
    /// <summary>
    /// The targeted key id, or null when <paramref name="centres"/> is empty. A null
    /// <paramref name="boxes"/> skips pass 1 and is plain nearest-centre.
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
        return nearestId;
    }
}
