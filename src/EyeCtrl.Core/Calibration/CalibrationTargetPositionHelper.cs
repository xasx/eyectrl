namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>Generates screen positions for calibration targets, distributed across the screen with margins.</summary>
public static class CalibrationTargetPositionHelper
{
    private const double Margin = 0.2;

    /// <summary>
    /// Generates a list of calibration target positions in physical screen pixels,
    /// ordered outermost-first for user-friendly scanning patterns.
    /// </summary>
    /// <param name="count">Number of calibration points (3–16).</param>
    /// <param name="screenSize">Screen dimensions in physical pixels.</param>
    /// <returns>Ordered list of target positions in physical screen pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is outside 3–16.</exception>
    public static IReadOnlyList<PhysicalPoint> GetPositions(int count, PhysicalPoint screenSize)
    {
        if (count < 3 || count > 16)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Point count must be between 3 and 16.");

        var w = screenSize.X;
        var h = screenSize.Y;
        var minX = w * Margin;
        var maxX = w * (1 - Margin);
        var minY = h * Margin;
        var maxY = h * (1 - Margin);
        var cx = w * 0.5;
        var cy = h * 0.5;

        List<PhysicalPoint> all;

        if (count == 3)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY),
                new(maxX, minY),
                new(cx, maxY),
            };
        }
        else if (count == 4)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else if (count == 5)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(cx, cy),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else
        {
            int cols, rows;
            if (count <= 9) { cols = 3; rows = 3; }
            else { cols = 4; rows = 4; }

            all = new List<PhysicalPoint>();
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var x = cols == 1 ? cx : minX + (maxX - minX) * c / (cols - 1);
                var y = rows == 1 ? cy : minY + (maxY - minY) * r / (rows - 1);
                all.Add(new PhysicalPoint(x, y));
            }
        }

        List<PhysicalPoint> selected;
        if (count >= all.Count)
        {
            selected = new List<PhysicalPoint>(all);
        }
        else
        {
            selected = new List<PhysicalPoint>(count);
            var seeds = new[]
            {
                new PhysicalPoint(minX, minY), new PhysicalPoint(maxX, minY),
                new PhysicalPoint(minX, maxY), new PhysicalPoint(maxX, maxY),
                new PhysicalPoint(cx, cy),
            };
            foreach (var seed in seeds)
            {
                if (selected.Count >= count) break;
                PhysicalPoint nearest = default;
                double nearestDist = double.MaxValue;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = DistSq(p, seed);
                    if (d < nearestDist) { nearestDist = d; nearest = p; }
                }
                selected.Add(nearest);
            }
            while (selected.Count < count)
            {
                PhysicalPoint farthest = default;
                double farthestDist = -1;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = double.MaxValue;
                    foreach (var s in selected) d = Math.Min(d, DistSq(p, s));
                    if (d > farthestDist) { farthestDist = d; farthest = p; }
                }
                selected.Add(farthest);
            }
        }
        selected.Sort((a, b) =>
        {
            var da = (a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy);
            var db = (b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy);
            return db.CompareTo(da);
        });

        var ordered = new List<PhysicalPoint>();
        var remaining = new List<PhysicalPoint>(selected);
        while (remaining.Count > 0)
        {
            ordered.Add(remaining[0]);
            remaining.RemoveAt(0);
            if (remaining.Count > 0)
            {
                ordered.Add(remaining[remaining.Count - 1]);
                remaining.RemoveAt(remaining.Count - 1);
            }
        }

        return ordered;
    }

    private static double DistSq(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
