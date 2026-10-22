namespace EyeCtrl.Core.Keyboard;

using EyeCtrl.Core.Models;

/// <summary>
/// The pure geometry behind E1's content-free slip line and saccade class. Nothing here returns
/// or accepts a key identity beyond the two rectangles and centres being compared, and nothing
/// it returns can be mapped back to a key: a yes/no, a compass point, a rounded distance in key
/// pitches, a coarse row band, and three saccade classes.
///
/// <para>
/// <b>Key pitch</b> is the smallest distance between any two key centres in the live layout (the
/// horizontal neighbour distance of an ordinary key), so every unit is the owner's real key size
/// at whatever multiplier is configured.
/// </para>
/// <para>
/// <b>Adjacency rule:</b> each key's box is inflated by 5 % of the pitch on every side (past the
/// half-gap that separates two keys), and two keys are adjacent when those inflated rectangles
/// touch or overlap. That includes diagonal contact (a shared corner) and the half-key stagger
/// between rows.
/// </para>
/// </summary>
public static class KeyboardE1Slip
{
    /// <summary>Per-side inflation of a key box, as a share of the key pitch.</summary>
    public const double AdjacencyInflation = 0.05;

    /// <summary>A saccade shorter than this many key pitches is <c>short</c>.</summary>
    public const double ShortSaccadePitches = 0.5;

    /// <summary>A saccade longer than this many key pitches is <c>long</c>; in between is <c>mid</c>.</summary>
    public const double LongSaccadePitches = 2.0;

    /// <summary>The longest distance reported, in key pitches.</summary>
    public const int MaxDistancePitches = 3;

    /// <summary>The value when something is unknown or does not apply.</summary>
    public const string None = "-";

    /// <summary>The key pitch: the smallest distance between two distinct key centres, or 0 for fewer than two.</summary>
    public static double Pitch(IEnumerable<PhysicalPoint> centres)
    {
        var points = centres as IList<PhysicalPoint> ?? centres.ToList();
        var best = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var d = DistanceSquared(points[i], points[j]);
                if (d > 0 && d < best) best = d;
            }
        }
        return best == double.MaxValue ? 0 : Math.Sqrt(best);
    }

    /// <summary><c>short</c> (&lt; 0.5 pitch), <c>mid</c> (0.5–2) or <c>long</c> (&gt; 2); <c>-</c> without a pitch.</summary>
    public static string SaccadeClass(double distancePx, double pitchPx)
    {
        if (pitchPx <= 0) return None;
        var pitches = distancePx / pitchPx;
        return pitches < ShortSaccadePitches ? "short" : pitches > LongSaccadePitches ? "long" : "mid";
    }

    /// <summary>Whether two keys' cells touch or overlap (shared edge, shared corner, or stagger overlap).</summary>
    public static bool Adjacent(PhysicalRect a, PhysicalRect b, double pitchPx)
    {
        var e = AdjacencyInflation * pitchPx;
        return a.X - e <= b.X + b.Width + e && b.X - e <= a.X + a.Width + e
            && a.Y - e <= b.Y + b.Height + e && b.Y - e <= a.Y + a.Height + e;
    }

    /// <summary>The 8-way compass point from one key centre to another (screen up is N); <c>-</c> for the same point.</summary>
    public static string Direction(PhysicalPoint from, PhysicalPoint to)
    {
        var dx = to.X - from.X;
        var dy = from.Y - to.Y;   // screen y grows downward
        if (dx == 0 && dy == 0) return None;
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        var sector = (int)Math.Round(angle / 45.0);
        var index = ((sector % 8) + 8) % 8;
        return index switch
        {
            0 => "E", 1 => "NE", 2 => "N", 3 => "NW", 4 => "W", 5 => "SW", 6 => "S", _ => "SE",
        };
    }

    /// <summary>The centre-to-centre distance in key pitches, rounded and capped at <see cref="MaxDistancePitches"/>.</summary>
    public static int DistancePitches(PhysicalPoint a, PhysicalPoint b, double pitchPx)
    {
        if (pitchPx <= 0) return 0;
        var pitches = Math.Sqrt(DistanceSquared(a, b)) / pitchPx;
        return (int)Math.Min(MaxDistancePitches, Math.Round(pitches));
    }

    /// <summary>
    /// The thirds of the rows that HOLD TEXT KEYS (on QWERTZ: the q-row is <c>top</c>, the a-row <c>mid</c>,
    /// the y-row <c>bot</c>; the Space/function row holds none and is not counted): <c>top</c>, <c>mid</c>
    /// or <c>bot</c>; <c>-</c> when unknown. <paramref name="textRowIndex"/> is the row's position among
    /// the text rows, not its index in the layout.
    /// </summary>
    public static string RowBand(int textRowIndex, int textRowCount)
    {
        if (textRowIndex < 0 || textRowCount <= 0 || textRowIndex >= textRowCount) return None;
        return (textRowIndex * 3 / textRowCount) switch { 0 => "top", 1 => "mid", _ => "bot" };
    }

    /// <summary>A row needs at least this many plain (text) keys to count as a text row.</summary>
    public const int MinTextKeysPerTextRow = 3;

    /// <summary>
    /// Which outer quarter of the key box the dwell's mean offset falls in, as one closed class:
    /// <c>N</c>/<c>NE</c>/<c>E</c>/<c>SE</c>/<c>S</c>/<c>SW</c>/<c>W</c>/<c>NW</c>, or <c>-</c> for the inner half
    /// (the centre) and for an unknown box. The box is split at a quarter of its width and height from each
    /// edge. This is the only gaze-offset fact a text key's line carries: it is all the edge-crowding
    /// rule needs, and unlike a pixel offset it cannot be calibrated into a column through the tracker's
    /// bias field (only a bias strong enough to push the gaze into the outer quarter shows).
    /// </summary>
    public static string EdgeQuarter(double meanDxPx, double meanDyPx, double boxWidthPx, double boxHeightPx)
    {
        if (boxWidthPx <= 0 || boxHeightPx <= 0) return None;
        var horizontal = meanDxPx > OuterQuarter * boxWidthPx ? "E" : meanDxPx < -OuterQuarter * boxWidthPx ? "W" : "";
        var vertical = meanDyPx > OuterQuarter * boxHeightPx ? "S" : meanDyPx < -OuterQuarter * boxHeightPx ? "N" : "";
        var quarter = vertical + horizontal;
        return quarter.Length == 0 ? None : quarter;
    }

    /// <summary>The outer quarter of a box side, as a share of its width/height.</summary>
    public const double OuterQuarter = 0.25;

    private static double DistanceSquared(PhysicalPoint a, PhysicalPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
